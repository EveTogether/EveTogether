using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Client.Esi;
using EveUtils.Client.Gamelog;
using EveUtils.Client.LocalApi.Dtos;
using EveUtils.Client.Platform;
using EveUtils.Client.Transport;
using EveUtils.Client.ViewModels.FitBrowser;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Fittings.Dtos;
using EveUtils.Shared.Modules.Fittings.Entities;
using EveUtils.Shared.Modules.Fittings.Repositories;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Transport;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.LocalApi;

/// <summary>
/// Read-only data access for the local API endpoints, reading the existing client services through the captured
/// root provider (the composition-root seam). Singleton client services are read directly; the scoped fitting repository gets a
/// per-call scope. Maps everything to the public, versioned DTOs — interns/entities and tokens never leave here.
/// </summary>
public sealed partial class LocalApiQueries(IServiceProvider rootServices)
{
    /// <summary>Live combat metrics for your own currently-running characters (gamelog-driven, fleet independent).</summary>
    public async Task<IReadOnlyList<CharacterMetricsDto>> GetMetricsAsync(CancellationToken cancellationToken = default)
    {
        var gamelog = rootServices.GetService<GamelogClientService>();
        if (gamelog is null) return [];

        var running = rootServices.GetService<EveClientPresenceService>()?.Current.CharacterNames ?? new HashSet<string>();
        var idByName = await _IdByNameAsync(cancellationToken);

        return running.Select(name =>
        {
            var rates = gamelog.SampleCombat(name);
            var snapshot = gamelog.Snapshot(name);
            return new CharacterMetricsDto(
                idByName.TryGetValue(name, out var id) ? id : null,
                name,
                Running: true,
                DpsOut: rates.Dealt,
                DpsIn: rates.Received,
                NeutPerSecond: rates.Neut,
                CapPerSecond: rates.Cap,
                BountyTotal: snapshot.BountyTotal,
                Kills: snapshot.Kills,
                Location: snapshot.Location,
                PeakDps: snapshot.PeakDealtDps);
        }).ToList();
    }

    /// <summary>Coupled characters with public identity (corp/alliance), a portrait URL and running state. No tokens.</summary>
    public async Task<IReadOnlyList<CharacterDto>> GetCharactersAsync(CancellationToken cancellationToken = default)
    {
        var registry = rootServices.GetService<ICharacterRegistry>();
        if (registry is null) return [];

        var info = rootServices.GetService<ICharacterInfoService>();
        var presence = rootServices.GetService<EveClientPresenceService>()?.Current;
        var characters = await registry.GetAllAsync(cancellationToken);

        return characters.Select(c =>
        {
            var pub = c.EsiCharacterId is { } id ? info?.GetCached(id) : null;
            var running = (c.EsiCharacterId is { } rid && (presence?.CharacterIds.Contains(rid) ?? false))
                          || (presence?.CharacterNames.Contains(c.Name) ?? false);
            return new CharacterDto(
                c.EsiCharacterId,
                c.Name,
                running,
                pub?.CorporationName,
                pub?.CorporationTicker,
                pub?.AllianceName,
                pub?.AllianceTicker,
                c.EsiCharacterId is { } pid ? $"https://images.evetech.net/characters/{pid}/portrait?size=256" : null);
        }).ToList();
    }

    /// <summary>The fit library across both sources: your local library plus each coupled server's shared fits.
    /// Each row carries its scope so a widget can tell a local fit from a server one. An unreachable server is skipped.</summary>
    public async Task<IReadOnlyList<FitSummaryDto>> GetFitsAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<FitSummaryDto>();
        result.AddRange(await _LocalFitsAsync(cancellationToken));
        result.AddRange(await _ServerFitsAsync(cancellationToken));
        return result;
    }

    private async Task<IReadOnlyList<FitSummaryDto>> _LocalFitsAsync(CancellationToken cancellationToken)
    {
        var names = FitNameResolverFactory.For(rootServices);
        await using var scope = rootServices.CreateAsyncScope();
        var fits = await scope.ServiceProvider.GetRequiredService<IFittingRepository>().ListAllAsync(cancellationToken);

        return fits.Select(f => new FitSummaryDto(
            f.Id, f.Name, f.ShipTypeId, names.TypeName(f.ShipTypeId), names.GroupName(f.ShipTypeId),
            "local", null, null)).ToList();
    }

    private async Task<IReadOnlyList<FitSummaryDto>> _ServerFitsAsync(CancellationToken cancellationToken)
    {
        var share = rootServices.GetService<ServerFitShareClient>();
        var sessions = rootServices.GetService<IClientSessionStore>();
        if (share is null || sessions is null) return [];

        var names = FitNameResolverFactory.For(rootServices);
        var servers = await sessions.ListServersAsync(cancellationToken);
        var loaded = await Task.WhenAll(servers.Select(server => _LoadServerFitsAsync(share, server, names, cancellationToken)));
        return loaded.SelectMany(x => x).ToList();
    }

    private async Task<IReadOnlyList<FitSummaryDto>> _LoadServerFitsAsync(
        ServerFitShareClient share, string server, ISdeNameResolver names, CancellationToken cancellationToken)
    {
        var serverName = await _ServerNameAsync(server, cancellationToken);
        // GetSharedFitsAsync already turns an RpcException into ok=false; the catch is a belt-and-suspenders skip.
        try
        {
            var (ok, _, fits) = await share.GetSharedFitsAsync(server, 0, cancellationToken);
            if (!ok) return [];
            return fits.Select(f => new FitSummaryDto(
                f.ServerId, f.Name, f.ShipTypeId, names.TypeName(f.ShipTypeId), names.GroupName(f.ShipTypeId),
                "server", server, serverName, f.SharedByCharacterName)).ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>One fit's detail; <paramref name="server"/> selects a coupled server's shared library (its id is the
    /// shared-fit id), null/empty resolves against the local library. <paramref name="includeStats"/> adds the Dogma
    /// stats (all-level-5). Null when the id is not found in that scope.</summary>
    public async Task<FitDetailDto?> GetFitAsync(int id, string? server, bool includeStats, CancellationToken cancellationToken = default)
    {
        var names = FitNameResolverFactory.For(rootServices);

        if (string.IsNullOrEmpty(server))
        {
            LocalFitting? fit;
            await using (var scope = rootServices.CreateAsyncScope())
                fit = await scope.ServiceProvider.GetRequiredService<IFittingRepository>().FindByIdAsync(id, cancellationToken);
            if (fit is null) return null;
            return await _BuildFitDetailAsync(_TryParse(fit.RawJson), fit.Id, fit.Name, fit.Description ?? string.Empty,
                fit.ShipTypeId, "local", null, null, includeStats, names, cancellationToken);
        }

        var share = rootServices.GetService<ServerFitShareClient>();
        if (share is null) return null;
        var (ok, _, fits) = await share.GetSharedFitsAsync(server, 0, cancellationToken);
        if (!ok || fits.FirstOrDefault(f => f.ServerId == id) is not { } shared) return null;
        var serverName = await _ServerNameAsync(server, cancellationToken);
        return await _BuildFitDetailAsync(_TryParse(shared.RawJson), shared.ServerId, shared.Name, string.Empty,
            shared.ShipTypeId, "server", server, serverName, includeStats, names, cancellationToken);
    }

    private async Task<FitDetailDto?> _BuildFitDetailAsync(
        EsiFitting? esi, int id, string name, string description, int shipTypeId,
        string scope, string? serverAddress, string? serverName, bool includeStats,
        ISdeNameResolver names, CancellationToken cancellationToken)
    {
        var items = esi?.Items.Select(i => new FitItemDto(i.TypeId, names.TypeName(i.TypeId), i.Flag, i.Quantity)).ToList()
                    ?? new List<FitItemDto>();

        FitStatsDto? stats = null;
        if (includeStats && esi is not null && rootServices.GetService<IFitStatsProvider>() is { } provider)
        {
            var computed = await provider.ComputeAsync(esi, cancellationToken);
            if (computed is not null) stats = FitStatsDto.FromStats(computed);
        }

        return new FitDetailDto(
            id, name, description, shipTypeId, names.TypeName(shipTypeId), names.GroupName(shipTypeId),
            scope, serverAddress, serverName, items, stats);
    }

    /// <summary>Resolves a type id to its name + group and a public CCP icon URL. Always
    /// returns a row — the resolver falls back to "type {id}" when the SDE has not been imported.</summary>
    public TypeInfoDto GetTypeInfo(int id)
    {
        var names = FitNameResolverFactory.For(rootServices);
        return new TypeInfoDto(id, names.TypeName(id), names.GroupName(id), $"https://images.evetech.net/types/{id}/icon");
    }

    private async Task<IReadOnlyDictionary<string, int>> _IdByNameAsync(CancellationToken cancellationToken)
    {
        var registry = rootServices.GetService<ICharacterRegistry>();
        if (registry is null) return new Dictionary<string, int>();
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in await registry.GetAllAsync(cancellationToken))
            if (c.EsiCharacterId is { } id)
                map[c.Name] = id;
        return map;
    }

    private static EsiFitting? _TryParse(string rawJson)
    {
        try { return JsonSerializer.Deserialize<EsiFitting>(rawJson); }
        catch { return null; }
    }
}
