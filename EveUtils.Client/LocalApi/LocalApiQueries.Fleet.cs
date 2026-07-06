using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Client.Fleet;
using EveUtils.Client.LocalApi.Dtos;
using EveUtils.Client.Transport;
using EveUtils.Client.ViewModels.FitBrowser;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Fleet.Composition.Repositories;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Repositories;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Transport;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.LocalApi;

/// <summary>
/// fleet + composition reads for the local API. Mirrors how <c>FleetsViewModel</c> aggregates fleets across both
/// sources — client-only fleets via the client-bound <see cref="IFleetRepository"/>, and per coupled server via
/// <see cref="IFleetTransportClient"/> — and how the roster windows build an <see cref="IFleetClient"/> per scope.
/// Read-only and snapshot-shaped: live per-member fleet metrics are a streamed signal, not a readable state.
/// </summary>
public sealed partial class LocalApiQueries
{
    /// <summary>Your fleets across every source: client-only local fleets plus, per coupled server, the active fleets
    /// you own or are a member of. Discoverable/open fleets you are not in are not included. An unreachable server is
    /// skipped rather than failing the whole list (mirrors the Fleets window).</summary>
    public async Task<IReadOnlyList<FleetListItemDto>> GetFleetsAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<FleetListItemDto>();
        result.AddRange(await _LocalFleetsAsync(cancellationToken));
        result.AddRange(await _ServerFleetsAsync(cancellationToken));
        return result;
    }

    private async Task<IReadOnlyList<FleetListItemDto>> _LocalFleetsAsync(CancellationToken cancellationToken)
    {
        var registry = rootServices.GetService<ICharacterRegistry>();
        if (registry is null) return [];

        var items = new List<FleetListItemDto>();
        var seen = new HashSet<long>();
        await using var scope = rootServices.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IFleetRepository>();

        foreach (var character in await registry.GetAllAsync(cancellationToken))
        {
            if (character.EsiCharacterId is not { } ownerId) continue;
            foreach (var fleet in await repository.ListByCreatorAsync(ownerId, cancellationToken))
            {
                // Only client-only, still-active, not-concluded fleets — the same filter the local Fleets tab applies.
                if (!fleet.IsClientOnly || fleet.State != FleetState.Active || fleet.Activation == FleetActivation.Concluded)
                    continue;
                if (!seen.Add(fleet.Id)) continue;
                items.Add(new FleetListItemDto(
                    fleet.Id, fleet.Name, fleet.Description, "local", null, null,
                    fleet.CreatorCharacterId, fleet.State.ToString(), fleet.Activation.ToString(),
                    fleet.Visibility.ToString(), fleet.IsClientOnly, fleet.FleetCompositionId));
            }
        }
        return items;
    }

    private async Task<IReadOnlyList<FleetListItemDto>> _ServerFleetsAsync(CancellationToken cancellationToken)
    {
        var transport = rootServices.GetService<IFleetTransportClient>();
        var sessions = rootServices.GetService<IClientSessionStore>();
        if (transport is null || sessions is null) return [];

        // Load every coupled server INDEPENDENTLY and CONCURRENTLY so a slow/dead server can't stall the others (the
        // same isolation FleetsViewModel.ReloadAsync needs).
        var servers = await sessions.ListServersAsync(cancellationToken);
        var loaded = await Task.WhenAll(servers.Select(server => _LoadServerFleetsAsync(transport, sessions, server, cancellationToken)));
        return loaded.SelectMany(x => x).ToList();
    }

    private async Task<IReadOnlyList<FleetListItemDto>> _LoadServerFleetsAsync(
        IFleetTransportClient transport, IClientSessionStore sessions, string server, CancellationToken cancellationToken)
    {
        var serverName = await _ServerNameAsync(server, cancellationToken);
        var items = new List<FleetListItemDto>();
        var seen = new HashSet<long>();
        try
        {
            // a fleet can hold several of my coupled characters — list it once (dedupe by fleet id).
            foreach (var session in await sessions.LoadAllAsync(server, cancellationToken))
                foreach (var fleet in (await transport.ListMyFleetsAsync(server, session.CharacterId, cancellationToken))
                             .Where(f => f.State == FleetState.Active))
                {
                    if (!seen.Add(fleet.Id)) continue;
                    items.Add(new FleetListItemDto(
                        fleet.Id, fleet.Name, fleet.Description, "server", server, serverName,
                        fleet.CreatorCharacterId, fleet.State.ToString(), fleet.Activation.ToString(),
                        fleet.Visibility.ToString(), false, fleet.FleetCompositionId));
                }
        }
        catch (FleetTransportException)
        {
            return []; // unreachable/stale server — isolate it from the rest of the sweep
        }
        return items;
    }

    /// <summary>The active fleet (the one this client is participating in) with its wing/squad structure and roster,
    /// or null when not participating. Resolves the roster through the same per-scope <see cref="IFleetClient"/> the
    /// roster windows use (local seam for a client-only fleet, gRPC for a server fleet).</summary>
    public async Task<FleetDetailDto?> GetActiveFleetAsync(CancellationToken cancellationToken = default)
    {
        var active = rootServices.GetService<IActiveFleetState>();
        if (active?.ActiveFleetId is not { } fleetId) return null;
        var actingCharacterId = active.CharacterId ?? 0;

        if (active.IsActiveFleetClientOnly)
        {
            await using var scope = rootServices.CreateAsyncScope();
            var client = _LocalFleetClient(scope, actingCharacterId);
            var compositions = _LocalCompositionClient(scope, actingCharacterId);
            return await _BuildFleetDetailAsync(client, compositions, fleetId, "local", null, null, cancellationToken);
        }

        var transport = rootServices.GetService<IFleetTransportClient>();
        if (active.ActiveServerAddress is not { } server || transport is null) return null;
        var serverName = await _ServerNameAsync(server, cancellationToken);
        var serverClient = new ServerFleetClient(transport, server, actingCharacterId);
        var serverCompositions = new ServerFleetCompositionClient(transport, server, actingCharacterId);
        return await _BuildFleetDetailAsync(serverClient, serverCompositions, fleetId, "server", server, serverName, cancellationToken);
    }

    private async Task<FleetDetailDto?> _BuildFleetDetailAsync(
        IFleetClient client, IFleetCompositionClient compositions, long fleetId,
        string scope, string? serverAddress, string? serverName, CancellationToken cancellationToken)
    {
        FleetInfo? info;
        IReadOnlyList<FleetMemberInfo> members;
        IReadOnlyList<FleetWingInfo> wings;
        IReadOnlyList<ConnectedCharacterInfo> connected;
        try
        {
            info = await client.GetFleetAsync(fleetId);
            if (info is null) return null;
            members = await client.ListMembersAsync(fleetId);
            wings = await client.ListWingsAsync(fleetId);
            connected = await client.ListConnectedCharactersAsync();
        }
        catch (FleetTransportException)
        {
            return null; // the active fleet's server went unreachable mid-read
        }

        var nameById = connected.ToDictionary(c => c.CharacterId, c => c.CharacterName);
        var names = FitNameResolverFactory.For(rootServices);

        var wingDtos = new List<FleetWingDto>();
        foreach (var wing in wings)
        {
            IReadOnlyList<FleetSquadInfo> squads;
            try { squads = await client.ListSquadsAsync(wing.Id); }
            catch (FleetTransportException) { squads = []; }
            wingDtos.Add(new FleetWingDto(wing.Id, wing.Name, squads.Select(s => new FleetSquadDto(s.Id, s.Name)).ToList()));
        }

        var memberDtos = members.Select(m => new FleetMemberDto(
            m.Id, m.CharacterId, nameById.GetValueOrDefault(m.CharacterId),
            m.WingId, m.SquadId, m.Role.ToString(), m.IsExternal,
            m.AssignedFit?.ShipTypeId,
            m.AssignedFit is { } fit ? names.TypeName(fit.ShipTypeId) : null,
            m.AssignedFit?.FitName)).ToList();

        string? compositionName = null;
        if (info.FleetCompositionId is { } compositionId)
        {
            try { compositionName = (await compositions.GetAsync(compositionId))?.Composition.Name; }
            catch (FleetTransportException) { /* best-effort doctrine name */ }
        }

        return new FleetDetailDto(
            info.Id, info.Name, info.Description, scope, serverAddress, serverName,
            info.CreatorCharacterId, info.State.ToString(), info.Activation.ToString(), info.Visibility.ToString(),
            scope == "local", info.FleetCompositionId, compositionName, wingDtos, memberDtos);
    }

    /// <summary>The doctrine/composition library across both sources: your local library plus each coupled server's
    /// library. An unreachable server is skipped.</summary>
    public async Task<IReadOnlyList<CompositionListItemDto>> GetCompositionsAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<CompositionListItemDto>();
        result.AddRange(await _LocalCompositionsAsync(cancellationToken));
        result.AddRange(await _ServerCompositionsAsync(cancellationToken));
        return result;
    }

    private async Task<IReadOnlyList<CompositionListItemDto>> _LocalCompositionsAsync(CancellationToken cancellationToken)
    {
        var registry = rootServices.GetService<ICharacterRegistry>();
        if (registry is null) return [];

        var items = new List<CompositionListItemDto>();
        var seen = new HashSet<long>();
        await using var scope = rootServices.CreateAsyncScope();
        foreach (var character in await registry.GetAllAsync(cancellationToken))
        {
            if (character.EsiCharacterId is not { } ownerId) continue;
            var client = _LocalCompositionClient(scope, ownerId);
            foreach (var info in await client.ListAllAsync())
            {
                if (!seen.Add(info.Id)) continue;
                items.Add(new CompositionListItemDto(
                    info.Id, info.Name, info.Description, "local", null, null,
                    info.OwnerCharacterId, info.OwnerName, info.FleetCount));
            }
        }
        return items;
    }

    private async Task<IReadOnlyList<CompositionListItemDto>> _ServerCompositionsAsync(CancellationToken cancellationToken)
    {
        var transport = rootServices.GetService<IFleetTransportClient>();
        var sessions = rootServices.GetService<IClientSessionStore>();
        if (transport is null || sessions is null) return [];

        var servers = await sessions.ListServersAsync(cancellationToken);
        var loaded = await Task.WhenAll(servers.Select(server => _LoadServerCompositionsAsync(transport, sessions, server, cancellationToken)));
        return loaded.SelectMany(x => x).ToList();
    }

    private async Task<IReadOnlyList<CompositionListItemDto>> _LoadServerCompositionsAsync(
        IFleetTransportClient transport, IClientSessionStore sessions, string server, CancellationToken cancellationToken)
    {
        var recent = await sessions.LoadAsync(server, cancellationToken);
        var serverName = await _ServerNameAsync(server, cancellationToken);
        var client = new ServerFleetCompositionClient(transport, server, recent?.CharacterId ?? 0);
        try
        {
            var infos = await client.ListAllAsync();
            return infos.Select(info => new CompositionListItemDto(
                info.Id, info.Name, info.Description, "server", server, serverName,
                info.OwnerCharacterId, info.OwnerName, info.FleetCount)).ToList();
        }
        catch (FleetTransportException)
        {
            return [];
        }
    }

    /// <summary>One composition's full tree (roles + fit-entries). <paramref name="server"/> selects a server's
    /// library; null/empty resolves against the local library. Null when the id is not found in that scope.</summary>
    public async Task<CompositionDetailDto?> GetCompositionAsync(long id, string? server, CancellationToken cancellationToken = default)
    {
        var names = FitNameResolverFactory.For(rootServices);

        if (string.IsNullOrEmpty(server))
        {
            var registry = rootServices.GetService<ICharacterRegistry>();
            if (registry is null) return null;
            await using var scope = rootServices.CreateAsyncScope();
            foreach (var character in await registry.GetAllAsync(cancellationToken))
            {
                if (character.EsiCharacterId is not { } ownerId) continue;
                var detail = await _LocalCompositionClient(scope, ownerId).GetAsync(id);
                if (detail is not null)
                    return _ToCompositionDetail(detail, "local", null, null, names);
            }
            return null;
        }

        var transport = rootServices.GetService<IFleetTransportClient>();
        var sessions = rootServices.GetService<IClientSessionStore>();
        if (transport is null || sessions is null) return null;
        var recent = await sessions.LoadAsync(server, cancellationToken);
        var client = new ServerFleetCompositionClient(transport, server, recent?.CharacterId ?? 0);
        try
        {
            var detail = await client.GetAsync(id);
            if (detail is null) return null;
            var serverName = await _ServerNameAsync(server, cancellationToken);
            return _ToCompositionDetail(detail, "server", server, serverName, names);
        }
        catch (FleetTransportException)
        {
            return null;
        }
    }

    private static CompositionDetailDto _ToCompositionDetail(
        FleetCompositionDetail detail, string scope, string? server, string? serverName, ISdeNameResolver names)
    {
        var roles = detail.Roles.Select(r => new CompositionRoleDto(
            r.Id, r.RoleName, r.GroupMinCount,
            r.Entries.Select(e => new CompositionEntryDto(
                e.Id, e.EntryMinCount, e.Fit.ShipTypeId, names.TypeName(e.Fit.ShipTypeId), e.Fit.FitName)).ToList())).ToList();
        var c = detail.Composition;
        return new CompositionDetailDto(
            c.Id, c.Name, c.Description, scope, server, serverName, c.OwnerCharacterId, c.OwnerName, roles);
    }

    private LocalFleetClient _LocalFleetClient(AsyncServiceScope scope, int actingCharacterId) =>
        new(rootServices.GetRequiredService<ClientFleetService>(),
            scope.ServiceProvider.GetRequiredService<IFleetRepository>(),
            rootServices.GetRequiredService<ICharacterRegistry>(),
            actingCharacterId);

    private LocalFleetCompositionClient _LocalCompositionClient(AsyncServiceScope scope, int actingCharacterId) =>
        new(rootServices.GetRequiredService<ClientFleetService>(),
            scope.ServiceProvider.GetRequiredService<IFleetCompositionRepository>(),
            actingCharacterId);

    private async Task<string?> _ServerNameAsync(string server, CancellationToken cancellationToken)
    {
        var registry = rootServices.GetService<IServerRegistry>();
        return registry is null ? server : await registry.DisplayNameAsync(server, cancellationToken);
    }
}
