using System.Collections.Generic;
using EveUtils.Client.Platform;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Metrics;

namespace EveUtils.Client.Fleet;

/// <summary>
/// The fleet <see cref="MetricKind.Presence"/> source (ET-70): whether this participating character is actually in
/// game, which <see cref="ILocalCharacterPresence"/> already knows and which nothing has ever told the rest of the
/// fleet. A member on another machine used to look identical whether they were parked in station with the game shut
/// or simply not sharing anything — a difference an FC steers on.
///
/// It emits <b>every</b> tick, including when the verdict is <see cref="PresenceState.Unknown"/>, and that is the
/// point rather than an oversight. The sample's arrival is itself the message "my EVE Together is running"; it is
/// what keeps the member's server-side <c>LastSeenAt</c> fresh, and so what makes the silence that follows a closed
/// client mean something. A source that fell quiet when it had no verdict would report the pilot as departed.
/// </summary>
public sealed class PresenceMetricSource(ILocalCharacterPresence? presence = null) : IFleetMetricSource, ISingletonService
{
    public IEnumerable<MetricSample> Sample(long fleetId, int characterId, long unixMs)
    {
        // null — no presence service, or the registry has not loaded yet — travels as Unknown rather than as
        // "not in game". Every consequence of a wrong "offline" hides something (a location, a place in the count),
        // so the state that claims nothing is the only safe one to guess (ET-71).
        var state = presence?.IsInGame(characterId) switch
        {
            true => PresenceState.InGame,
            false => PresenceState.NotInGame,
            null => PresenceState.Unknown,
        };

        yield return new MetricSample(characterId, fleetId, MetricKind.Presence, (double)state, unixMs);
    }
}
