using System;
using System.Collections.Generic;
using EveUtils.Client.Gamelog;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Metrics;

namespace EveUtils.Client.Fleet;

/// <summary>
/// The fleet <see cref="MetricKind.Location"/> source. A member's position is sensitive, so — unlike
/// DPS — it is opt-IN by default. That privacy gate is the same one every metric runs through: the publisher's
/// per-metric share decision (<see cref="MetricShareSnapshot.IsShared(long,int,MetricKind)"/>, which defaults
/// Location to off). This source therefore just produces the value and lets the publisher gate it — uniform with
/// DPS: on/off per server fleet, and always shared in a local-only fleet where the share-gate does not apply.
///
/// The value is a State sample carrying the participating character's solar system id in <see cref="MetricSample.Value"/>
/// (ET-394) and its name in <see cref="MetricSample.Text"/>. The name stays because a client from before ET-394 reads
/// only the name and keeps the newest sample: a sample without one would blank the member's system on its screen.
/// The id is 0 when the SDE does not know the name yet; a receiver then falls back to the name. Until a system is
/// known for the participating character, the source emits nothing rather than a fabricated position.
/// </summary>
public sealed class LocationMetricSource(GamelogClientService gamelog, SolarSystemIdResolver systemIds)
    : IFleetMetricSource, ISingletonService
{
    /// <summary>Settings key for the location privacy opt-in. Absent/anything-but-"true" means opted out.</summary>
    public const string ShareLocationSettingKey = "fleet.share-location";

    public IEnumerable<MetricSample> Sample(long fleetId, int characterId, long unixMs)
    {
        if (gamelog.LocationOf(characterId) is not { } location)
            yield break;

        var abyssalAnchorMs = location.AbyssalAnchor is { } anchor
            ? new DateTimeOffset(anchor, TimeSpan.Zero).ToUnixTimeMilliseconds()
            : 0;
        yield return new MetricSample(characterId, fleetId, MetricKind.Location, systemIds.Resolve(location.System) ?? 0,
            unixMs, location.System, abyssalAnchorMs);
    }
}
