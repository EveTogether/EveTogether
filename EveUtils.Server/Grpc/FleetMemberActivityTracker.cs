using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Fleet.Metrics;
using EveUtils.Shared.Modules.Fleet.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Server.Grpc;

/// <summary>
/// Keeps each member's <c>LastSeenAt</c> fresh from live fleet traffic — <see cref="FleetActivityTracker"/>'s pattern
/// one level down, from the fleet to the member (ET-70). The fleet-level clock cannot answer this: one pilot still
/// publishing keeps the whole fleet's <c>LastActivityAt</c> current while everybody else has closed their client.
///
/// This is the half of "who is offline" no message can report. A client that is shut down never sends anything saying
/// so, so the only evidence is its traffic stopping, and the only place that can see traffic stop for a pilot on
/// somebody else's machine is here. What a client CAN say — the game is closed while EVE Together runs — travels as
/// <see cref="MetricKind.Presence"/> on the same stream and needs none of this.
///
/// Writes are throttled per (fleet, member) to <see cref="FleetMemberPresence.SeenWriteThrottle"/>, half the silence
/// window: the stored row is what a freshly-opened screen reads before it has heard a sample of its own, so it may
/// never be stale enough to look like silence on its own.
/// </summary>
public sealed class FleetMemberActivityTracker(IServiceProvider services) : ISingletonService
{
    private readonly ConcurrentDictionary<(long FleetId, int CharacterId), DateTimeOffset> _lastNoted = new();

    public async Task NoteAsync(long fleetId, int characterId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        if (characterId == 0)
            return;

        var shouldTouch = false;
        _lastNoted.AddOrUpdate(
            (fleetId, characterId),
            _ => { shouldTouch = true; return now; },
            (_, previous) =>
            {
                if (now - previous < FleetMemberPresence.SeenWriteThrottle)
                    return previous;
                shouldTouch = true;
                return now;
            });

        if (!shouldTouch)
            return;

        using var scope = services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IFleetRepository>();
        await repository.TouchMemberSeenAsync(fleetId, characterId, now, cancellationToken);
    }
}
