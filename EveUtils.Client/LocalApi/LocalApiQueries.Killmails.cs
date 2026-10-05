using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Client.Fleet;
using EveUtils.Client.Killmails;
using EveUtils.Client.LocalApi.Dtos;
using EveUtils.Client.ViewModels.FitBrowser;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Esi;
using EveUtils.Shared.Modules.Killmails.Dtos;
using EveUtils.Shared.Modules.Killmails.Queries;
using EveUtils.Shared.Modules.Killmails.Repositories;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Settings.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.LocalApi;

public sealed partial class LocalApiQueries
{
    // Owned by the "Include my location" setting (ET-432); read here by key so the two do not share a type.
    private const string IncludeLocationSettingKey = "localapi.includelocation";
    private const int MaxLatestKillmails = 25;

    /// <summary>A mail stored this long after it happened is still "new" to a stream overlay; anything older is a
    /// backfill of history and is never announced.</summary>
    internal static readonly TimeSpan NewKillmailWindow = TimeSpan.FromHours(1);

    /// <summary>Your newest kills and/or losses across every coupled character, newest first, a mail two of your
    /// characters share listed once.</summary>
    public async Task<IReadOnlyList<KillmailLatestDto>> GetLatestKillmailsAsync(
        KillmailsLatestKind kind, int limit, CancellationToken cancellationToken = default)
    {
        var rows = await _KillmailRowsAsync(null, cancellationToken);
        var wanted = rows
            .Where(row => kind switch
            {
                KillmailsLatestKind.Kills => !row.IsLoss,
                KillmailsLatestKind.Losses => row.IsLoss,
                _ => true
            })
            .DistinctBy(row => row.KillmailId)
            .OrderByDescending(row => row.KillmailTimeUtc)
            .Take(Math.Clamp(limit, 1, MaxLatestKillmails))
            .ToList();
        return await _ToLatestDtosAsync(wanted, cancellationToken);
    }

    /// <summary>The just-stored mails among <paramref name="addedKillmailIds"/> that are recent enough to announce.
    /// Empty for a backfill of old history, so an import never floods a stream overlay.</summary>
    public async Task<IReadOnlyList<KillmailLatestDto>> GetNewKillmailsAsync(
        int characterId, IReadOnlyCollection<int> addedKillmailIds, CancellationToken cancellationToken = default)
    {
        var cutoff = (rootServices.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow().UtcDateTime - NewKillmailWindow;
        var rows = (await _KillmailRowsAsync(characterId, cancellationToken))
            .Where(row => addedKillmailIds.Contains(row.KillmailId) && row.KillmailTimeUtc >= cutoff)
            .ToList();
        return rows.Count == 0 ? [] : await _ToLatestDtosAsync(rows, cancellationToken);
    }

    private async Task<IReadOnlyList<KillmailOverviewRowDto>> _KillmailRowsAsync(int? characterId, CancellationToken cancellationToken)
    {
        await using var scope = rootServices.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<IDispatcher>()
            .Query(new GetKillmailsOverviewQuery(characterId), cancellationToken);
        return result.IsSuccess && result.Value is not null ? result.Value : [];
    }

    private async Task<IReadOnlyList<KillmailLatestDto>> _ToLatestDtosAsync(
        IReadOnlyList<KillmailOverviewRowDto> rows, CancellationToken cancellationToken)
    {
        if (rows.Count == 0
            || rootServices.GetService<ISdeAccessor>() is not { } sde
            || rootServices.GetService<IEsiAffiliationResolver>() is not { } affiliation)
            return [];

        await using var scope = rootServices.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        var settings = provider.GetRequiredService<ISettingRepository>();
        var includeLocation = (await settings.ListAsync(cancellationToken))
            .FirstOrDefault(setting => setting.Key == IncludeLocationSettingKey)?.Value == "true";

        var own = (await provider.GetRequiredService<ICharacterRegistry>().GetAllAsync(cancellationToken))
            .Where(character => character.EsiCharacterId is > 0)
            .GroupBy(character => character.EsiCharacterId!.Value)
            .ToDictionary(group => group.Key, group => group.First().Name);
        var names = new KillmailNames(own, provider.GetService<IExternalCharacterLookup>(), affiliation, sde,
            provider.GetRequiredService<IKillmailEntityNameRepository>(), settings,
            provider.GetService<TimeProvider>() ?? TimeProvider.System);

        await names.HydrateAsync(
            rows.SelectMany(row => new[] { row.VictimCharacterId, row.FinalBlow?.CharacterId }).OfType<int>(),
            rows.SelectMany(row => new[] { row.VictimCorporationId, row.FinalBlow?.CorporationId }).OfType<int>(),
            [], cancellationToken);

        var types = FitNameResolverFactory.For(rootServices);
        return rows.Select(row => new KillmailLatestDto(
            row.KillmailId,
            row.IsLoss ? "Loss" : "Kill",
            row.CharacterId,
            names.NameOf(row.CharacterId),
            DateTime.SpecifyKind(row.KillmailTimeUtc, DateTimeKind.Utc),
            row.VictimShipTypeId,
            types.TypeName(row.VictimShipTypeId),
            row.IskValue,
            row.AttackerCount,
            row.RunId,
            row.VictimCharacterId is { } victim ? names.NameOf(victim) : null,
            row.VictimCorporationId is { } corporation ? names.NameOf(corporation) : null,
            row.FinalBlow is { } blow
                ? new KillmailFinalBlowNameDto(
                    blow.CharacterId is { } blowCharacter ? names.NameOf(blowCharacter) : null,
                    blow.CorporationId is { } blowCorporation ? names.NameOf(blowCorporation) : null,
                    blow.ShipTypeId,
                    blow.ShipTypeId is { } blowShip ? types.TypeName(blowShip) : null)
                : null,
            includeLocation ? row.SolarSystemId : null,
            includeLocation ? sde.GetSolarSystem(row.SolarSystemId)?.Name : null)).ToList();
    }
}
