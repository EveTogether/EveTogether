using EveUtils.Shared.Cqrs;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Events;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Grouping;

namespace EveUtils.Client.Fleet;

public sealed class FleetRunGroupCodeCoordinator : ISingletonService, IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<(RunGroupKey Key, long CharacterId), RunningRun> _runs = [];
    private readonly Dictionary<RunGroupKey, List<RunGroupCodeCandidate>> _candidates = [];
    private readonly SemaphoreSlim _reconcileGate = new(1, 1);
    private readonly IDispatcher _dispatcher;
    private readonly IDisposable _runStartedSubscription;
    private readonly IDisposable _runSavedSubscription;
    private readonly IDisposable _groupCodeSubscription;
    private readonly IDisposable _discardedSubscription;

    public FleetRunGroupCodeCoordinator(IEventBus eventBus, IDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _runStartedSubscription = eventBus.Subscribe<RunStartedEvent>(_OnRunStartedAsync);
        _runSavedSubscription = eventBus.Subscribe<RunSavedEvent>(_OnRunSavedAsync);
        _groupCodeSubscription = eventBus.Subscribe<FleetRunGroupCodeEvent>(_OnGroupCodeAsync);
        _discardedSubscription = eventBus.Subscribe<FleetRunDiscardedEvent>(_OnDiscardedAsync);
    }

    public void Dispose()
    {
        _runStartedSubscription.Dispose();
        _runSavedSubscription.Dispose();
        _groupCodeSubscription.Dispose();
        _discardedSubscription.Dispose();
        _reconcileGate.Dispose();
    }

    private async Task _OnRunStartedAsync(RunStartedEvent integrationEvent, CancellationToken cancellationToken)
    {
        RunStartedEventData started = integrationEvent.Data;
        if (started.FleetId is not { } fleetId)
            return;

        RunningRun run = new(started.RunId, started.CharacterId,
            RunGroupKey.For(fleetId, started.ActivityKind, started.SolarSystemName, started.SiteName),
            started.GroupCode, started.StartedAtUtc, started.IsFleetCommander);
        lock (_gate)
        {
            _runs[(run.Key, run.CharacterId)] = run;
            _AddCandidate(run);
        }

        await _ReconcileAsync(run, cancellationToken);
    }

    private Task _OnRunSavedAsync(RunSavedEvent integrationEvent, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            RunningRun? run = _runs.Values.FirstOrDefault(candidate => candidate.RunId == integrationEvent.Data);
            if (run is null)
                return Task.CompletedTask;

            _runs.Remove((run.Key, run.CharacterId));
            if (!_runs.Values.Any(candidate => candidate.Key == run.Key))
                _candidates.Remove(run.Key);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// The fleet commander ended the run. Every client applies it to its own rows only — the command matches on the
    /// group code, and the only runs in this database are this pilot's, so no machine ever writes another's data.
    /// A member who already saved keeps their run; it is merely unlinked (ET-105 AC-1).
    /// </summary>
    private async Task _OnDiscardedAsync(FleetRunDiscardedEvent integrationEvent, CancellationToken cancellationToken)
    {
        RunGroupDiscard discard = integrationEvent.Data;
        await _dispatcher.Send(new DiscardRunsInGroupCommand(discard.GroupCode, discard.DiscardedAtUtc),
            cancellationToken);

        lock (_gate)
        {
            // A discard names a group code, not a site, so it ends the groups holding that code — not every group
            // this fleet has running (ET-136).
            List<RunGroupKey> ended = [.. _candidates
                .Where(entry => entry.Key.FleetId == discard.FleetId
                                && entry.Key.ActivityKind == discard.ActivityKind
                                && entry.Value.Any(candidate => candidate.GroupCode == discard.GroupCode))
                .Select(entry => entry.Key)];

            foreach (RunGroupKey key in ended)
            {
                foreach ((RunGroupKey Key, long CharacterId) runKey in _runs.Keys.Where(entry => entry.Key == key).ToList())
                    _runs.Remove(runKey);
                _candidates.Remove(key);
            }
        }
    }

    private async Task _OnGroupCodeAsync(FleetRunGroupCodeEvent integrationEvent, CancellationToken cancellationToken)
    {
        RunGroupCodeStart start = integrationEvent.Data;
        RunGroupKey key = RunGroupKey.For(start.FleetId, start.ActivityKind, start.SolarSystemName, start.SiteName);
        RunGroupCodeCandidate candidate = new(start.GroupCode, start.StartedAtUtc, start.IsFleetCommander);
        List<RunningRun> runs;
        lock (_gate)
        {
            _AddCandidate(key, candidate);
            runs = [.. _runs.Values.Where(run => run.Key == key)];
        }

        foreach (RunningRun run in runs)
            await _ReconcileAsync(run, cancellationToken);
    }

    private async Task _ReconcileAsync(RunningRun run, CancellationToken cancellationToken)
    {
        await _reconcileGate.WaitAsync(cancellationToken);
        try
        {
            List<RunGroupCodeCandidate> candidates;
            lock (_gate)
                candidates = [.. _candidates.GetValueOrDefault(run.Key, [])];

            Result<string> selected = RunGroupCodeArbiter.Select(run.Key.ActivityKind, candidates);
            if (!selected.IsSuccess || selected.Value is not { } groupCode || run.GroupCode == groupCode)
                return;

            if (run.GroupCode is not null)
            {
                Result unlinked = await _dispatcher.Send(new UnlinkRunFromGroupCodeCommand(run.RunId), cancellationToken);
                if (!unlinked.IsSuccess)
                    return;
            }

            Result linked = await _dispatcher.Send(new LinkRunToGroupCodeCommand(run.RunId, groupCode), cancellationToken);
            if (!linked.IsSuccess)
                return;

            lock (_gate)
                _runs[(run.Key, run.CharacterId)] = run with { GroupCode = groupCode };
        }
        finally
        {
            _reconcileGate.Release();
        }
    }

    private void _AddCandidate(RunningRun run)
    {
        if (run.GroupCode is not null)
            _AddCandidate(run.Key, new RunGroupCodeCandidate(run.GroupCode, run.StartedAtUtc, run.IsFleetCommander));
    }

    private void _AddCandidate(RunGroupKey key, RunGroupCodeCandidate candidate)
    {
        if (!_candidates.TryGetValue(key, out List<RunGroupCodeCandidate>? candidates))
        {
            candidates = [];
            _candidates.Add(key, candidates);
        }

        if (!candidates.Any(existing => existing.GroupCode == candidate.GroupCode))
            candidates.Add(candidate);
    }

    /// <summary>
    /// Who may share one group code: the same fleet, doing the same kind of thing, in the same system, on the same
    /// site (ET-136). Fleet and activity kind alone filed six pilots on six anomalies as one shared run.
    ///
    /// The site is its name, never the scan id: EVE gives each pilot their own id for the same site, so keying on
    /// the id would keep two members on ONE site apart — the opposite of what this key is for. <c>_IsSameRun</c> in
    /// ActivityWindowViewModel does key on the scan id, because it asks the other question: whether one pilot's two
    /// runs are the same run. Two questions, two keys; do not merge them.
    ///
    /// ponytail: two instances of the same site in one system (two Sansha Refuges side by side) still share a key.
    /// Wrong, and knowingly left — no source today tells the instances apart. Split it when one exists.
    /// </summary>
    private readonly record struct RunGroupKey(
        long FleetId,
        ActivityKind ActivityKind,
        string? SolarSystem,
        string? Site)
    {
        public static RunGroupKey For(long fleetId, ActivityKind activityKind, string? solarSystem, string? site) =>
            new(fleetId, activityKind, _Normalize(solarSystem), _Normalize(site));

        // Both halves are text a pilot copied; casing or padding must not split a group that belongs together.
        private static string? _Normalize(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();
    }

    private sealed record RunningRun(
        Guid RunId,
        long CharacterId,
        RunGroupKey Key,
        string? GroupCode,
        DateTime StartedAtUtc,
        bool IsFleetCommander);
}
