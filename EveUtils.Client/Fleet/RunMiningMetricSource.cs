using System.Collections.Concurrent;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Metrics;

namespace EveUtils.Client.Fleet;

/// <summary>
/// What this client's run has mined, offered to the fleet as <see cref="MetricKind.MiningYield"/> (ET-234). Opt-IN at
/// the publisher's share gate, so nothing here decides who may see it — this only produces the figure, the same
/// division <see cref="RunLootMetricSource"/> works to.
///
/// The figure is pushed in by the run window through <see cref="SetMinedUnits"/> rather than read here: MINING already
/// sums this run's own <c>RunMiningEntry</c> rows, and <c>Sample</c> must not block.
/// </summary>
public sealed class RunMiningMetricSource : IFleetMetricSource, ISingletonService
{
    private readonly ConcurrentDictionary<int, int?> _unitsByCharacter = new();

    /// <summary>
    /// What a character's run has mined so far, crit included, residue not. Null is "there is no such figure" — no
    /// run on the clock — and a null is not sent at all: a zero on somebody else's screen reads as "they mined
    /// nothing", which is a different statement from "nothing was measured" (ET-65 AC-5's rule, the same
    /// <see cref="RunLootMetricSource"/> follows).
    /// </summary>
    public void SetMinedUnits(int characterId, int? minedUnits)
    {
        if (characterId != 0)
            _unitsByCharacter[characterId] = minedUnits;
    }

    public IEnumerable<MetricSample> Sample(long fleetId, int characterId, long unixMs)
    {
        if (_unitsByCharacter.TryGetValue(characterId, out int? units) && units is { } mined)
            yield return new MetricSample(characterId, fleetId, MetricKind.MiningYield, mined, unixMs);
    }
}
