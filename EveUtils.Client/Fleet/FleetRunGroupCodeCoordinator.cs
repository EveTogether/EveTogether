using EveUtils.Shared.Cqrs;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Events;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Grouping;

namespace EveUtils.Client.Fleet;

public sealed class FleetRunGroupCodeCoordinator : ISingletonService, IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<(long FleetId, ActivityKind ActivityKind, long CharacterId), RunningRun> _runs = [];
    private readonly Dictionary<(long FleetId, ActivityKind ActivityKind), List<RunGroupCodeCandidate>> _candidates = [];
    private readonly SemaphoreSlim _reconcileGate = new(1, 1);
    private readonly IDispatcher _dispatcher;
    private readonly IDisposable _runStartedSubscription;
    private readonly IDisposable _groupCodeSubscription;

    public FleetRunGroupCodeCoordinator(IEventBus eventBus, IDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _runStartedSubscription = eventBus.Subscribe<RunStartedEvent>(_OnRunStartedAsync);
        _groupCodeSubscription = eventBus.Subscribe<FleetRunGroupCodeEvent>(_OnGroupCodeAsync);
    }

    public void Dispose()
    {
        _runStartedSubscription.Dispose();
        _groupCodeSubscription.Dispose();
        _reconcileGate.Dispose();
    }

    private async Task _OnRunStartedAsync(RunStartedEvent integrationEvent, CancellationToken cancellationToken)
    {
        RunStartedEventData started = integrationEvent.Data;
        if (started.FleetId is not { } fleetId)
            return;

        RunningRun run = new(started.RunId, started.CharacterId, fleetId, started.ActivityKind, started.GroupCode,
            started.StartedAtUtc, started.IsFleetCommander);
        lock (_gate)
        {
            _runs[(fleetId, started.ActivityKind, started.CharacterId)] = run;
            _AddCandidate(run);
        }

        await _ReconcileAsync(run, cancellationToken);
    }

    private async Task _OnGroupCodeAsync(FleetRunGroupCodeEvent integrationEvent, CancellationToken cancellationToken)
    {
        RunGroupCodeCandidate candidate = new(integrationEvent.Data.GroupCode, integrationEvent.Data.StartedAtUtc,
            integrationEvent.Data.IsFleetCommander);
        List<RunningRun> runs;
        lock (_gate)
        {
            _AddCandidate(integrationEvent.Data.FleetId, integrationEvent.Data.ActivityKind, candidate);
            runs = _runs.Values
                .Where(run => run.FleetId == integrationEvent.Data.FleetId && run.ActivityKind == integrationEvent.Data.ActivityKind)
                .ToList();
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
                candidates = [.. _candidates.GetValueOrDefault((run.FleetId, run.ActivityKind), [])];

            Result<string> selected = RunGroupCodeArbiter.Select(run.ActivityKind, candidates);
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
                _runs[(run.FleetId, run.ActivityKind, run.CharacterId)] = run with { GroupCode = groupCode };
        }
        finally
        {
            _reconcileGate.Release();
        }
    }

    private void _AddCandidate(RunningRun run)
    {
        if (run.GroupCode is not null)
            _AddCandidate(run.FleetId, run.ActivityKind,
                new RunGroupCodeCandidate(run.GroupCode, run.StartedAtUtc, run.IsFleetCommander));
    }

    private void _AddCandidate(long fleetId, ActivityKind activityKind, RunGroupCodeCandidate candidate)
    {
        (long FleetId, ActivityKind ActivityKind) key = (fleetId, activityKind);
        if (!_candidates.TryGetValue(key, out List<RunGroupCodeCandidate>? candidates))
        {
            candidates = [];
            _candidates.Add(key, candidates);
        }

        if (!candidates.Any(existing => existing.GroupCode == candidate.GroupCode))
            candidates.Add(candidate);
    }

    private sealed record RunningRun(
        Guid RunId,
        long CharacterId,
        long FleetId,
        ActivityKind ActivityKind,
        string? GroupCode,
        DateTime StartedAtUtc,
        bool IsFleetCommander);
}
