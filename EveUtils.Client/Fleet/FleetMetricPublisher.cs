using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Modules.Fleet.Metrics;
using EveUtils.Shared.DependencyInjection;

namespace EveUtils.Client.Fleet;

/// <summary>
/// Drives the ~1 Hz fleet activity stream. For every (character, fleet) in <see cref="IFleetParticipation"/>,
/// each tick polls every <see cref="IFleetMetricSource"/> for that scope and publishes each sample as a
/// <see cref="FleetMetricEvent"/>: the local UI always graphs it, and — when the share-gate allows it — the server
/// reroutes it, fleet-scoped, to the fleet's other active participants. With an empty set the tick is a no-op, so
/// nothing leaks to a fleet the user has left.
///
/// Membership is the gate, not an explicit enter: this read <c>IActiveFleetState</c> until the server dropped the
/// same Enter-driven model (<c>FleetBroadcastResolver</c>), and the doc said so for a while after the code stopped
/// doing it — which is how ET-152 came to be written against a publisher that had already moved.
///
/// The client has no generic host, so <see cref="Start"/>/<see cref="StopAsync"/> own the loop manually (like
/// <c>ClientTokenRefreshService</c>). The unit of work, <see cref="PublishTickAsync"/>, is public and
/// deterministic so a headless check can drive it without the timer.
/// </summary>
public sealed class FleetMetricPublisher(
    IFleetParticipation participation,
    IEnumerable<IFleetMetricSource> sources,
    IEventBus eventBus,
    IMetricShareSettings shareSettings,
    FleetMemberActivityTracker memberActivity) : ISingletonService
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

            // For a server fleet the server stamps LastSeenAt off the arriving stream; a client-only fleet's samples
            // never leave this machine, so nobody would ever stamp it and the local roster would have no record of
            // presence at all. Same tracker, same throttle, written into the local database instead (ET-167) — which
            // is what lets the next start of this app tell a fleet that stood idle for days from one closed a minute
            // ago. Unshared metrics are irrelevant here: the tick arriving is the evidence, not what is in it.
            if (participant.ClientOnly)
                await memberActivity.NoteAsync(
                    participant.FleetId,
                    participant.CharacterId,
                    DateTimeOffset.FromUnixTimeMilliseconds(unixMs),
                    cancellationToken);

            foreach (var source in _sources)
            foreach (var sample in source.Sample(participant.FleetId, participant.CharacterId, unixMs))
            {
                // The share-gate decides what LEAVES this machine, never what your own client draws: an unshared
                // sample drops to Local so your own row and the fleet totals still get it (ET-41). A client-only
                // fleet is already Local, so the gate is moot there.
                var sampleTarget = share.IsShared(participant.FleetId, participant.CharacterId, sample.Kind)
                    ? target
                    : EventTarget.Local;

                await eventBus.PublishAsync(new FleetMetricEvent(sample, participant.CharacterId), sampleTarget, cancellationToken);
            }
        }
    }
}
