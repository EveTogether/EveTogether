using System.Collections.Concurrent;
using System.Collections.Generic;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Metrics;

namespace EveUtils.Client.Fleet;

/// <summary>
/// The fleet <see cref="MetricKind.Location"/> source. A member's position is sensitive, so — unlike
/// DPS — it is opt-IN by default. That privacy gate is the same one every metric runs through: the publisher's
/// per-metric share decision (<see cref="MetricShareSnapshot.IsShared(long,int,MetricKind)"/>, which defaults
/// Location to off). This source therefore just produces the value and lets the publisher gate it — uniform with
/// DPS: on/off per
/// server fleet, and always shared in a local-only fleet where the share-gate does not apply.
///
/// The value is a State sample carrying the participating character's <c>solar_system_id</c>, which the receiver
/// resolves to a name via the SDE. The id itself is fed in via <see cref="SetSystem"/> (today from the gamelog
/// jump/undock path once an id is resolvable; an ESI location poll could feed it later) — until a system is known
/// for the participating character, the source emits nothing rather than a fabricated position.
/// </summary>
public sealed class LocationMetricSource : IFleetMetricSource, ISingletonService
{
    /// <summary>Settings key for the location privacy opt-in. Absent/anything-but-"true" means opted out.</summary>
    public const string ShareLocationSettingKey = "fleet.share-location";

    // The last known solar-system id per participating character id; the seam fed by the location source.
    private readonly ConcurrentDictionary<int, long> _systemByCharacter = new();

    /// <summary>Set the current solar-system id for a character; the next shared tick emits it.</summary>
    public void SetSystem(int characterId, long solarSystemId)
    {
        if (characterId != 0 && solarSystemId != 0)
            _systemByCharacter[characterId] = solarSystemId;
    }

    public IEnumerable<MetricSample> Sample(long fleetId, int characterId, long unixMs)
    {
        // No privacy gate here: the publisher's per-metric share decision (Location defaults to opt-IN) is the single
        // gate, and a local-only fleet bypasses it entirely. We only emit when an actual position is known.
        if (!_systemByCharacter.TryGetValue(characterId, out var solarSystemId))
            yield break;

        yield return new MetricSample(characterId, fleetId, MetricKind.Location, solarSystemId, unixMs);
    }
}
