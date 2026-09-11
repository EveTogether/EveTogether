using System;
using System.Collections.Generic;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Events;

namespace EveUtils.Client.Fleet;

/// <summary>
/// Every pilot's own leg of a shared run, as each announced it (ET-243): the starts off <c>fleet.run-group</c> — the
/// commander's and every member's own — and the ways out off <c>fleet.run-group.pilot-stopped</c>, by group code and
/// character. What a run window whose clock is per pilot builds the fleet's clock from, first pilot in to last one out.
///
/// Held app-wide rather than by the window, so a window opened late, or reopened mid-run, still knows who went in
/// before it existed. Kept for the session only; a discarded run's legs go with it.
/// </summary>
public sealed class FleetRunLegs : ISingletonService, IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Dictionary<int, FleetRunLeg>> _legs = new(StringComparer.Ordinal);
    private readonly IDisposable _startedSubscription;
    private readonly IDisposable _stoppedSubscription;
    private readonly IDisposable _resumedSubscription;
    private readonly IDisposable _discardedSubscription;

    public FleetRunLegs(IEventBus eventBus)
    {
        _startedSubscription = eventBus.Subscribe<FleetRunGroupCodeEvent>(integrationEvent =>
            _NoteStarted(integrationEvent.Data.GroupCode, integrationEvent.CharacterId, integrationEvent.Data.StartedAtUtc));
        _stoppedSubscription = eventBus.Subscribe<FleetRunPilotStoppedEvent>(integrationEvent =>
            _NoteStopped(integrationEvent.Data.GroupCode, integrationEvent.CharacterId, integrationEvent.Data.StoppedAtUtc));
        // A resume (ET-250) is read exactly like a fresh leg's start — the same "in again" fact, so one pilot who
        // stops and starts again is not left reading "out" on everybody else's FLEET line until the 20-minute cut-off.
        _resumedSubscription = eventBus.Subscribe<FleetRunPilotResumedEvent>(integrationEvent =>
            _NoteStarted(integrationEvent.Data.GroupCode, integrationEvent.CharacterId, integrationEvent.Data.StartedAtUtc));
        _discardedSubscription = eventBus.Subscribe<FleetRunDiscardedEvent>(integrationEvent =>
        {
            lock (_gate)
                _legs.Remove(integrationEvent.Data.GroupCode);
        });
    }

    public void Dispose()
    {
        _startedSubscription.Dispose();
        _stoppedSubscription.Dispose();
        _resumedSubscription.Dispose();
        _discardedSubscription.Dispose();
    }

    /// <summary>The legs heard under <paramref name="groupCode"/>, one per character. Character 0 is a leg whose
    /// announcement named nobody.</summary>
    public IReadOnlyList<FleetRunLeg> Of(string groupCode)
    {
        lock (_gate)
            return _legs.TryGetValue(groupCode, out Dictionary<int, FleetRunLeg>? legs) ? [.. legs.Values] : [];
    }

    // The earliest start a pilot announced stands; a later one is a leg picked back up, which only means they are in.
    private void _NoteStarted(string groupCode, int? characterId, DateTime startedAtUtc)
    {
        int key = characterId ?? 0;
        lock (_gate)
        {
            Dictionary<int, FleetRunLeg> legs = _LegsOf(groupCode);
            legs[key] = legs.TryGetValue(key, out FleetRunLeg known) && known.StartedAtUtc <= startedAtUtc
                ? known with { StoppedAtUtc = null }
                : new FleetRunLeg(key, startedAtUtc, null);
        }
    }

    private void _NoteStopped(string groupCode, int? characterId, DateTime stoppedAtUtc)
    {
        int key = characterId ?? 0;
        lock (_gate)
            if (_legs.TryGetValue(groupCode, out Dictionary<int, FleetRunLeg>? legs)
                && legs.TryGetValue(key, out FleetRunLeg known))
                legs[key] = known with { StoppedAtUtc = stoppedAtUtc };
    }

    private Dictionary<int, FleetRunLeg> _LegsOf(string groupCode)
    {
        if (!_legs.TryGetValue(groupCode, out Dictionary<int, FleetRunLeg>? legs))
            _legs[groupCode] = legs = [];
        return legs;
    }
}

/// <summary>One pilot's own time in a run whose clock is per pilot (ET-243); still in while <see cref="StoppedAtUtc"/>
/// is null.</summary>
public readonly record struct FleetRunLeg(int CharacterId, DateTime StartedAtUtc, DateTime? StoppedAtUtc);
