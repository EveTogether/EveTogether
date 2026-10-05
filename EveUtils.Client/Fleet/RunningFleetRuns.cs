using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Events;

namespace EveUtils.Client.Fleet;

/// <summary>
/// The fleet runs a commander has going right now, as far as this client heard (ET-440) — what "Join fleet run" offers
/// a member who was not there at the start, declined the offer, or was cut off by a discard. Fed by the commander's
/// start and by the <see cref="FleetRunRunningEvent"/> heartbeat their window repeats while the run goes; a discard
/// or a stop takes the run off.
///
/// A run is believed for <see cref="HeartbeatLapse"/> after its last heartbeat. A commander on a client from before the
/// heartbeat never sends one, so their start alone is believed for <see cref="StartOnlyLapse"/> — long enough to join a
/// run in progress, short enough that a run ended without a word does not linger all evening.
/// </summary>
public sealed class RunningFleetRuns : ISingletonService, IDisposable
{
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HeartbeatLapse = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan StartOnlyLapse = TimeSpan.FromMinutes(30);

    private readonly Dictionary<string, (RunGroupCodeStart Start, DateTimeOffset HeardAt, bool IsHeartbeat)> _runs =
        new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly IDisposable[] _subscriptions;

    public RunningFleetRuns(IEventBus eventBus)
    {
        _subscriptions =
        [
            eventBus.Subscribe<FleetRunGroupCodeEvent>(integrationEvent => _Heard(integrationEvent.Data, isHeartbeat: false)),
            eventBus.Subscribe<FleetRunRunningEvent>(integrationEvent => _Heard(integrationEvent.Data, isHeartbeat: true)),
            eventBus.Subscribe<FleetRunDiscardedEvent>(integrationEvent => _Ended(integrationEvent.Data.GroupCode)),
            eventBus.Subscribe<FleetRunStoppedEvent>(integrationEvent => _Ended(integrationEvent.Data.GroupCode)),
        ];
    }

    /// <summary>The commander's runs of <paramref name="fleetId"/> still believed to be going, newest first.</summary>
    public IReadOnlyList<RunGroupCodeStart> Of(long fleetId, DateTimeOffset now)
    {
        lock (_gate)
            return [.. _runs.Values
                .Where(run => run.Start.FleetId == fleetId
                              && now - run.HeardAt < (run.IsHeartbeat ? HeartbeatLapse : StartOnlyLapse))
                .OrderByDescending(run => run.Start.StartedAtUtc)
                .Select(run => run.Start)];
    }

    public void Dispose()
    {
        foreach (IDisposable subscription in _subscriptions)
            subscription.Dispose();
    }

    private void _Heard(RunGroupCodeStart start, bool isHeartbeat)
    {
        if (!start.IsFleetCommander)
            return;

        lock (_gate)
            _runs[start.GroupCode] = (start, DateTimeOffset.UtcNow,
                isHeartbeat || (_runs.TryGetValue(start.GroupCode, out var held) && held.IsHeartbeat));
    }

    private void _Ended(string groupCode)
    {
        lock (_gate)
            _runs.Remove(groupCode);
    }
}
