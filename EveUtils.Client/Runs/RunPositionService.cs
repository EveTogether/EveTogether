using System.Collections.Concurrent;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Earnings;
using EveUtils.Shared.Modules.Runs.Events;
using EveUtils.Shared.Modules.Runs.Queries;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.Runs;

/// <summary>
/// A saved run against its activity's figures (ET-520). The figures are the ones the runs overview reads
/// (<see cref="IActivityStatsSource.ReadKeyAsync"/>): the same source rules as the panel, a server's when enough pilots
/// share and the pilot's own runs otherwise, with the reason. The run is valued at today's price like every run in
/// them, so it and they are on one scale.
///
/// <para>A position is kept for <see cref="ActivityStatsService.CacheFor"/>, the interval a server's figures are kept
/// for, so clicking through runs of one activity costs one read, not one per click. Any change to a run drops them
/// all: a saved, edited or deleted run, and a re-valuation, each moves the run's yield or the pilot's own figures.</para>
/// </summary>
public sealed class RunPositionService : IRunPositionSource, ISingletonService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IActivityStatsSource _stats;
    private readonly ISharedServerPicks _picks;
    private readonly ConcurrentDictionary<string, Cached> _positions = new(StringComparer.Ordinal);
    private int _generation;

    public RunPositionService(IServiceScopeFactory scopeFactory, IActivityStatsSource stats, ISharedServerPicks picks, IEventBus eventBus)
    {
        _scopeFactory = scopeFactory;
        _stats = stats;
        _picks = picks;
        eventBus.Subscribe<RunsChangedEvent>(_ =>
        {
            Interlocked.Increment(ref _generation);
            _positions.Clear();
        });
    }

    public async Task<RunPosition?> ReadAsync(string activityRef, IReadOnlyList<int> characterIds,
        CancellationToken cancellationToken = default)
    {
        string cacheKey = $"{activityRef}|{string.Join(',', characterIds)}";
        if (_positions.TryGetValue(cacheKey, out Cached? cached) && cached.IsFresh)
            return cached.Position;

        int generation = Volatile.Read(ref _generation);
        RunPosition? position = await _ReadAsync(activityRef, characterIds, cancellationToken);
        // A run changed while this was being read: what was read may already be out of date, so it is shown but not kept.
        if (generation == Volatile.Read(ref _generation))
            _positions[cacheKey] = new Cached(position);
        return position;
    }

    public async Task<IReadOnlyDictionary<string, RunVsTypical>> ReadVsTypicalAsync(IReadOnlyCollection<string> activityRefs,
        IReadOnlyList<int> characterIds, CancellationToken cancellationToken = default)
    {
        if (activityRefs.Count == 0)
            return new Dictionary<string, RunVsTypical>();

        IReadOnlyDictionary<string, ActivityYieldDto> yields;
        await using (AsyncServiceScope scope = _scopeFactory.CreateAsyncScope())
            yields = (await scope.ServiceProvider.GetRequiredService<IDispatcher>()
                .Query(new GetActivityYieldsQuery(activityRefs), cancellationToken)).Value ?? new Dictionary<string, ActivityYieldDto>();

        // One read per activity, not per run: the figures are the activity's and come from the caches the drawer shares.
        Dictionary<ActivityStatsKey, EarningsFiguresDto?> figures = [];
        foreach (ActivityStatsKey key in yields.Values.Select(yield => yield.Key).Distinct())
            figures[key] = (await _stats.ReadKeyAsync(key, characterIds, cancellationToken)).Basis?.Figures;

        Dictionary<string, RunVsTypical> against = [];
        foreach ((string activityRef, ActivityYieldDto run) in yields)
            if (run.IsCounted && figures[run.Key] is { } basis && RunVsTypical.Of(run.YieldIsk, basis) is { } standing)
                against[activityRef] = standing;
        return against;
    }

    private async Task<RunPosition?> _ReadAsync(string activityRef, IReadOnlyList<int> characterIds, CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        IDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
        if ((await dispatcher.Query(new GetActivityYieldQuery(activityRef), cancellationToken)).Value is not { } run)
            return null;

        ActivityStats read = await _stats.ReadKeyAsync(run.Key, characterIds, cancellationToken);
        if (read.Basis is not { } basis)
            return null;

        // The figure the overview shows may come from another server than the one this run belongs to (ET-512 decision 10).
        if (basis.ServerAddress is not null && read.Answers.Count > 1)
            basis = SharedServerChoice.Pick(read.Answers, await _ServerOfRunAsync(dispatcher, activityRef, cancellationToken),
                await _picks.LoadAsync(cancellationToken));

        return RunPosition.From(activityRef, run, basis, read.Answers, read.Reason);
    }

    private static async Task<string?> _ServerOfRunAsync(IDispatcher dispatcher, string activityRef, CancellationToken cancellationToken) =>
        (await dispatcher.Query(new GetOutcomeSharesQuery(DateTime.UtcNow, [activityRef]), cancellationToken)).Value?
        .FirstOrDefault(share => share.ActivityRef == activityRef)?.PreferredServer;

    private sealed class Cached(RunPosition? position)
    {
        private readonly DateTime _atUtc = DateTime.UtcNow;

        public RunPosition? Position { get; } = position;

        public bool IsFresh => DateTime.UtcNow - _atUtc < ActivityStatsService.CacheFor;
    }
}
