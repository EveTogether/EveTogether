using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.DependencyInjection;

namespace EveUtils.Client.Fleet;

/// <summary>
/// Drives the ~1 Hz fleet activity stream. While the client is participating in a fleet
/// (<see cref="IActiveFleetState"/>), each tick polls every <see cref="IFleetMetricSource"/> for the active
/// scope and publishes each sample as a <see cref="FleetMetricEvent"/> with <see cref="EventTarget.Both"/>: the
/// local UI graphs it, and the server reroutes it — fleet-scoped — to the fleet's other active participants
/// . When not participating the tick is a no-op, so nothing leaks to a fleet the user has left.
///
/// The client has no generic host, so <see cref="Start"/>/<see cref="StopAsync"/> own the loop manually (like
/// <c>ClientTokenRefreshService</c>). The unit of work, <see cref="PublishTickAsync"/>, is public and
/// deterministic so a headless check can drive it without the timer.
/// </summary>
public sealed class FleetMetricPublisher(
    IFleetParticipation participation,
    IEnumerable<IFleetMetricSource> sources,
    IEventBus eventBus,
    IMetricShareSettings shareSettings) : ISingletonService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    private readonly IFleetMetricSource[] _sources = sources.ToArray();
    private CancellationTokenSource? _loopCts;
    private Task? _loop;

    /// <summary>Begins the 1 Hz publish loop on a background task. Idempotent-ish: a second call is ignored.</summary>
    public void Start()
    {
        if (_loop is not null)
            return;

        _loopCts = new CancellationTokenSource();
        _loop = RunLoopAsync(_loopCts.Token);
    }

    public async Task StopAsync()
    {
        if (_loopCts is null)
            return;

        await _loopCts.CancelAsync();
        try
        {
            if (_loop is not null)
                await _loop;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _loopCts.Dispose();
            _loopCts = null;
            _loop = null;
        }
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(cancellationToken))
            await PublishTickAsync(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), cancellationToken);
    }

    /// <summary>
    /// One tick: if the client is participating, publish every source's samples for the active fleet. A no-op
    /// when there is no active fleet (or no participating character).
    ///
    /// All local characters active in the fleet are bundled into this <b>single</b> tick: each is polled
    /// across every source and its samples are stamped with that character's own id, so two toons in one fleet
    /// flush together over the one client→server stream rather than via N uncoordinated per-character timers. DPS
    /// is never merged — each character keeps its own per-character sample.
    /// </summary>
    public async Task PublishTickAsync(long unixMs, CancellationToken cancellationToken = default)
    {
        // Membership-driven: publish for every (character, fleet) the client is currently in — a member of a
        // connected-server fleet, or a client-only fleet — rather than an explicit "entered" fleet. Snapshot once so
        // the whole tick is a coherent flush (no mid-tick churn).
        var participants = participation.Current;
        if (participants.Count == 0)
            return;

        // One settings read per tick: which metric kinds the user currently shares (per-metric opt-out, location opt-in).
        var share = await shareSettings.LoadAsync(cancellationToken);

        foreach (var participant in participants)
        {
            // A client-only fleet lives purely in this client — its samples feed the local graphs only and
            // are NEVER pushed over gRPC. A server-backed fleet keeps the reroute (Both: local UI + server).
            var target = participant.ClientOnly ? EventTarget.Local : EventTarget.Both;

            foreach (var source in _sources)
            foreach (var sample in source.Sample(participant.FleetId, participant.CharacterId, unixMs))
            {
                // The share-gate is a privacy boundary for what you broadcast to OTHER members on a server (per-metric
                // opt-out, location opt-in). A client-only fleet is purely local — the samples only ever feed
                // your own graphs (EventTarget.Local above), so there is no one to hide them from: share everything
                // regardless of the gate. The per-fleet override / global default applies only to server-backed fleets.
                if (!participant.ClientOnly &&
                    !share.IsShared(participant.FleetId, participant.CharacterId, sample.Kind))
                    continue;

                await eventBus.PublishAsync(new FleetMetricEvent(sample, participant.CharacterId), target, cancellationToken);
            }
        }
    }
}
