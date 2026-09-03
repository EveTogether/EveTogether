using System.Collections.Concurrent;
using System.Collections.Generic;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Metrics;

namespace EveUtils.Client.Fleet;

/// <summary>
/// What this client's run has looted, offered to the fleet as <see cref="MetricKind.Loot"/>. Opt-IN at the
/// publisher's share gate, so nothing here decides who may see it — this only produces the figure, the same
/// division <see cref="LocationMetricSource"/> works to.
///
/// Loot only, and pointedly not bounty: <c>GamelogClientService</c> has emitted <see cref="MetricKind.Bounty"/> on
/// this same stream all along, per fleet run, and a second producer for one kind would put two figures on one row.
///
/// The figure is pushed in by the run window through <see cref="SetLootIsk"/> rather than read here: that window is
/// where it already sits, priced from the market cache by type id, and <c>Sample</c> must not block. The seam is
/// <see cref="LocationMetricSource.SetSystem"/>'s.
/// </summary>
public sealed class RunLootMetricSource : IFleetMetricSource, ISingletonService
{
    private readonly ConcurrentDictionary<int, decimal?> _lootByCharacter = new();

    /// <summary>
    /// What a character's run has looted right now, net of what it cost them. Null is "there is no such figure" —
    /// no run on the clock, or a price cache that could value nothing — and a null is not sent at all: a zero on
    /// somebody else's screen reads as "they found nothing", which is a different statement from "nothing was
    /// measured" (ET-65 AC-5's rule).
    /// </summary>
    public void SetLootIsk(int characterId, decimal? lootIsk)
    {
        if (characterId != 0)
            _lootByCharacter[characterId] = lootIsk;
    }

    public IEnumerable<MetricSample> Sample(long fleetId, int characterId, long unixMs)
    {
        if (_lootByCharacter.TryGetValue(characterId, out decimal? loot) && loot is { } isk)
            yield return new MetricSample(characterId, fleetId, MetricKind.Loot, (double)isk, unixMs);
    }
}
