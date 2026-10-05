using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Client.Calendar;
using EveUtils.Client.LocalApi.Dtos;
using EveUtils.Client.Opsec;
using EveUtils.Client.Runs;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Queries;
using EveUtils.Shared.Modules.Sde;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using CqrsDispatcher = EveUtils.Shared.Cqrs.IDispatcher;

namespace EveUtils.Client.LocalApi;

/// <summary>
/// The runs endpoints and pushes (ET-436): the activity on the clock, and the run totals per period and kind. Totals
/// are counted by <see cref="RunTotals"/> over the same facts the home and the runs overview build
/// (<see cref="RunsActivityFacts"/>), so a widget and the screen show one figure.
/// </summary>
public sealed class LocalApiRuns(IServiceProvider rootServices, bool includeLocation)
{
    /// <summary>"Include my location" (ET-432): a signature id or a system only leaves the app with this on.</summary>
    public const string IncludeLocationSettingKey = "localapi.includelocation";

    private readonly RunRowFacts _facts = new(rootServices.GetService<ISdeAccessor>());

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/runs/current", _CurrentAsync)
            .WithSummary("The activities on the clock right now")
            .WithDescription("One entry per activity running now (runs flown together are one), newest first; empty when "
                             + "nothing runs. ISK is what the activity earned so far: bounty as it is paid, loot as it is "
                             + "captured. signature and system stay null unless \"Include my location\" is on and OPSEC is off. "
                             + "Pushed as run.changed.");
        app.MapGet("/api/v1/runs/summary", _SummaryAsync)
            .WithSummary("Run totals over a period")
            .WithDescription("Runs, flown time, the own characters' ISK and ISK/hour — the figures the app's home and runs "
                             + "overview show. period: session (since the app started, or its last reset) | today | week | "
                             + "month, default today. kind: all | abyssal | combat | mission | mining, default all. "
                             + "kind=abyssal adds a breakdown per tier and weather. All combinations are pushed as runs.summary.");
    }

    /// <summary>Null when the runs could not be read.</summary>
    public async Task<IReadOnlyList<CurrentRunDto>?> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        CqrsDispatcher dispatcher = rootServices.GetRequiredService<CqrsDispatcher>();
        Result<IReadOnlyList<RunningRunDto>> running = await dispatcher.Query(new GetRunningRunsQuery(), cancellationToken);
        Result<IReadOnlyList<RunningActivityDto>> earned = await dispatcher.Query(new GetRunningActivitiesQuery(), cancellationToken);
        if (!running.IsSuccess || !earned.IsSuccess)
            return null;

        IReadOnlyDictionary<long, string> names = _Names(await _OwnCharactersAsync(cancellationToken));
        Dictionary<string, RunningActivityDto> activities = (earned.Value ?? []).ToDictionary(activity => activity.ActivityKey);
        RunningAbyssalPockets pockets = rootServices.GetRequiredService<RunningAbyssalPockets>();
        bool exposesLocation = _ExposesLocation();
        return [.. (running.Value ?? [])
            .GroupBy(run => run.GroupCode ?? run.Id.ToString())
            .Select(activity => _Current([.. activity.OrderBy(run => run.StartedAtUtc)],
                activities.GetValueOrDefault(activity.Key), names, pockets, exposesLocation))
            .OrderByDescending(run => run.StartedAtUtc)];
    }

    /// <summary>Null when the runs could not be read.</summary>
    public async Task<RunsSummaryDto?> GetSummaryAsync(RunsPeriod period, RunsKind kind, CancellationToken cancellationToken = default) =>
        (await _SummariesAsync([period], [kind], cancellationToken))?.Single();

    /// <summary>Every period and kind at once, off one read — what <c>runs.summary</c> pushes. Null when the runs could
    /// not be read.</summary>
    public Task<IReadOnlyList<RunsSummaryDto>?> GetSummariesAsync(CancellationToken cancellationToken = default) =>
        _SummariesAsync(Enum.GetValues<RunsPeriod>(), Enum.GetValues<RunsKind>(), cancellationToken);

    /// <summary>One period and kind over the activities read: counted by <see cref="RunTotals"/>, over the window
    /// <see cref="RunsPeriods"/> sets — the two things the home's earnings tiles count through as well.</summary>
    internal static RunsSummaryDto Summarise(IReadOnlyList<RunsActivityFacts> activities, RunsPeriod period, RunsKind kind,
        DateTime nowLocal, DayOfWeek firstDay, DateTime sessionStartLocal, IReadOnlyDictionary<long, string> ownCharacters,
        RunsBestDropDto? bestDrop)
    {
        DateTime fromLocal = RunsPeriods.StartOf(period, nowLocal, firstDay, sessionStartLocal);
        RunsActivityFacts[] counted = RunTotals.StartedBetween(activities.Where(activity => kind.Matches(activity.TypeId)), fromLocal, nowLocal);
        RunTotalsFigures totals = RunTotals.Of(counted);
        return new RunsSummaryDto(
            NameOf(period),
            NameOf(kind),
            fromLocal.ToUniversalTime(),
            totals.Runs,
            (long)totals.Flown.TotalSeconds,
            totals.Net,
            totals.PerHour,
            totals.Net is { } net && totals.Runs > 0 ? net / totals.Runs : null,
            _AverageSeconds(counted),
            counted.Count(activity => activity.HasShipLoss),
            kind is RunsKind.All ? bestDrop : null,
            _Characters(counted, ownCharacters),
            kind is RunsKind.Abyssal ? _Abyssal(counted) : null);
    }

    /// <summary>The query-string word for a period or kind: its name, lower case.</summary>
    internal static string NameOf<TEnum>(TEnum value) where TEnum : struct, Enum => value.ToString().ToLowerInvariant();

    /// <summary>An absent value is the default; anything but a member's name is refused — never a number.</summary>
    internal static bool TryParse<TEnum>(string? text, TEnum fallback, out TEnum value) where TEnum : struct, Enum
    {
        value = string.IsNullOrEmpty(text)
            ? fallback
            : Enum.GetValues<TEnum>().FirstOrDefault(member => string.Equals(NameOf(member), text, StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrEmpty(text) || string.Equals(NameOf(value), text, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<Results<Ok<IReadOnlyList<CurrentRunDto>>, ProblemHttpResult>> _CurrentAsync(
        LocalApiRuns runs, CancellationToken cancellationToken) =>
        await runs.GetCurrentAsync(cancellationToken) is { } current
            ? TypedResults.Ok(current)
            : TypedResults.Problem("The runs could not be read.", statusCode: StatusCodes.Status503ServiceUnavailable);

    private static async Task<Results<Ok<RunsSummaryDto>, ProblemHttpResult>> _SummaryAsync(
        string? period, string? kind, LocalApiRuns runs, CancellationToken cancellationToken)
    {
        if (!TryParse(period, RunsPeriod.Today, out RunsPeriod parsedPeriod))
            return TypedResults.Problem("period must be one of session, today, week, month.", statusCode: StatusCodes.Status400BadRequest);
        if (!TryParse(kind, RunsKind.All, out RunsKind parsedKind))
            return TypedResults.Problem("kind must be one of all, abyssal, combat, mission, mining.", statusCode: StatusCodes.Status400BadRequest);

        return await runs.GetSummaryAsync(parsedPeriod, parsedKind, cancellationToken) is { } summary
            ? TypedResults.Ok(summary)
            : TypedResults.Problem("The runs could not be read.", statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    private async Task<IReadOnlyList<RunsSummaryDto>?> _SummariesAsync(IReadOnlyList<RunsPeriod> periods,
        IReadOnlyList<RunsKind> kinds, CancellationToken cancellationToken)
    {
        CqrsDispatcher dispatcher = rootServices.GetRequiredService<CqrsDispatcher>();
        DateTime nowLocal = DateTime.Now;
        DayOfWeek firstDay = rootServices.GetRequiredService<IWeekStartService>().FirstDay;
        DateTime sessionStartLocal = rootServices.GetRequiredService<RunsSession>().StartedAtUtc.ToLocalTime();
        Dictionary<RunsPeriod, DateTime> starts = periods.ToDictionary(period => period,
            period => RunsPeriods.StartOf(period, nowLocal, firstDay, sessionStartLocal));

        IReadOnlyList<Character> characters = await _OwnCharactersAsync(cancellationToken);
        long[] ownIds = [.. characters.Select(character => character.EsiCharacterId).OfType<int>().Where(id => id > 0).Select(id => (long)id)];
        DateTime fromUtc = starts.Values.Min().ToUniversalTime();
        Result<IReadOnlyList<ActivityOverviewRowDto>> overview =
            await dispatcher.Query(new GetActivityOverviewQuery(fromUtc, OwnCharacterIds: ownIds), cancellationToken);
        if (!overview.IsSuccess)
            return null;

        RunsActivityFacts[] activities = [.. (overview.Value ?? []).Select(row => RunsActivityFacts.From(row, _facts))];
        IReadOnlyDictionary<long, string> names = _Names(characters);
        List<RunsSummaryDto> summaries = [];
        foreach ((RunsPeriod period, DateTime startLocal) in starts)
        {
            RunsBestDropDto? bestDrop = kinds.Contains(RunsKind.All)
                ? await _BestDropAsync(dispatcher, startLocal.ToUniversalTime(), ownIds, cancellationToken)
                : null;
            summaries.AddRange(kinds.Select(kind =>
                Summarise(activities, period, kind, nowLocal, firstDay, sessionStartLocal, names, bestDrop)));
        }

        return summaries;
    }

    private CurrentRunDto _Current(IReadOnlyList<RunningRunDto> runs, RunningActivityDto? earned,
        IReadOnlyDictionary<long, string> names, RunningAbyssalPockets pockets, bool exposesLocation)
    {
        RunningRunDto first = runs[0];
        (int Tier, string Weather)? pocket = runs.Select(run => pockets.Of(run.Id)).FirstOrDefault(known => known is not null);
        return new CurrentRunDto(
            first.Id,
            first.GroupCode,
            NameOf(first.ActivityKind),
            _facts.TypeOf(first.ActivityKind, first.SignatureGroupSnapshot, first.SiteTypeId, first.SiteName).Name,
            first.SiteName,
            pocket?.Tier,
            pocket is { } known ? _TierName(known.Tier) : null,
            pocket?.Weather,
            first.StartedAtUtc,
            earned?.BountyIsk ?? 0m,
            earned?.LootIskNet,
            earned is { Isk.HasFigure: true } ? earned.Isk.Total : null,
            [.. runs.Select(run => run.CharacterId).Distinct().Select(id => new CurrentRunCrewDto(id, names.GetValueOrDefault(id)))],
            exposesLocation ? first.Signature : null,
            exposesLocation ? _facts.SystemOf(earned?.SolarSystemId)?.Name : null);
    }

    private bool _ExposesLocation() => includeLocation && rootServices.GetService<IOpsecService>()?.IsEnabled != true;

    private async Task<IReadOnlyList<Character>> _OwnCharactersAsync(CancellationToken cancellationToken) =>
        await rootServices.GetRequiredService<ICharacterRegistry>().GetAllAsync(cancellationToken);

    private static IReadOnlyDictionary<long, string> _Names(IReadOnlyList<Character> characters) =>
        characters.Where(character => character.EsiCharacterId is > 0)
            .GroupBy(character => (long)(character.EsiCharacterId ?? 0))
            .ToDictionary(group => group.Key, group => group.First().Name);

    private static async Task<RunsBestDropDto?> _BestDropAsync(CqrsDispatcher dispatcher, DateTime fromUtc,
        IReadOnlyList<long> ownIds, CancellationToken cancellationToken)
    {
        Result<IReadOnlyList<BestDropDto>> drops = await dispatcher.Query(new GetBestDropsQuery(fromUtc, ownIds, 1), cancellationToken);
        return drops is { IsSuccess: true, Value: [var best, ..] }
            ? new RunsBestDropDto(best.TypeId, best.Name, best.Quantity, best.Value)
            : null;
    }

    /// <summary>Each own character's share, the way the runs summary's BY CHARACTER splits it (ET-294).</summary>
    private static IReadOnlyList<RunsCharacterTotalDto> _Characters(IReadOnlyList<RunsActivityFacts> activities,
        IReadOnlyDictionary<long, string> ownCharacters) =>
        [.. ownCharacters
            .Select(character => (character, shares: activities
                .Select(activity => activity.ShareOf(character.Key, ownCharacters.ContainsKey))
                .OfType<decimal>()
                .ToArray()))
            .Where(entry => entry.shares.Length > 0)
            .Select(entry => new RunsCharacterTotalDto(entry.character.Key, entry.character.Value, entry.shares.Sum()))];

    private static IReadOnlyList<AbyssalBreakdownDto> _Abyssal(IEnumerable<RunsActivityFacts> activities) =>
        [.. activities
            .GroupBy(activity => AbyssalFilamentName.Parse(activity.AbyssalFilamentText))
            .OrderBy(pocket => pocket.Key?.Tier ?? int.MaxValue)
            .ThenBy(pocket => pocket.Key?.Weather, StringComparer.Ordinal)
            .Select(pocket =>
            {
                RunsActivityFacts[] runs = [.. pocket];
                RunTotalsFigures totals = RunTotals.Of(runs);
                return new AbyssalBreakdownDto(pocket.Key?.Tier, pocket.Key is { } known ? _TierName(known.Tier) : null,
                    pocket.Key?.Weather, totals.Runs, totals.Net, totals.PerHour, _AverageSeconds(runs));
            })];

    /// <summary>Over the runs with a flown time: one left without a stop reads 0 and would pull the average down.</summary>
    private static long? _AverageSeconds(IEnumerable<RunsActivityFacts> activities)
    {
        double[] timed = [.. activities.Where(activity => activity.Duration > TimeSpan.Zero).Select(activity => activity.Duration.TotalSeconds)];
        return timed.Length == 0 ? null : (long)timed.Average();
    }

    private static string? _TierName(int tier) =>
        tier >= 0 && tier < AbyssalTiers.Names.Count ? AbyssalTiers.Names[tier] : null;
}
