namespace EveUtils.Shared.Modules.Fleet.Metrics;

/// <summary>
/// What a fleet activity sample measures. Deliberately broad and open-for-extension: new kinds
/// (salvage, reps, …) can be added without a protocol change. v1 actively produces <see cref="Dps"/> and
/// <see cref="DpsIn"/>; the rest are reserved examples whose semantics are already declared in
/// <see cref="FleetMetricCatalog"/>.
/// </summary>
public enum MetricKind
{
    /// <summary>Damage dealt per second (the outgoing DPS series of a member's live graph).</summary>
    Dps = 0,
    MiningYield = 1,
    Bounty = 2,
    Location = 3,

    /// <summary>Damage received per second (the incoming DPS series). The paired counterpart of <see cref="Dps"/>,
    /// carried as its own kind so the single-scalar sample envelope stays generic.</summary>
    DpsIn = 4,

    /// <summary>Energy neutralized per second (cap-warfare activity, both applied and received combined). A Rate,
    /// drawn as its own line on a member's live combat graph alongside <see cref="Dps"/>/<see cref="DpsIn"/>.</summary>
    Neut = 5,

    /// <summary>Running total of ore mined this session, sourced from the ESI mining-ledger endpoint. A
    /// Cumulative rollup into the fleet's total haul. Reserved: the descriptor travels the bus now; the ESI
    /// integration that feeds it is a later seam.</summary>
    MiningLedger = 6,

    /// <summary>Remote capacitor transmitted per second (cap support activity, given and received combined). A Rate,
    /// drawn as its own line on a member's live combat graph.</summary>
    Cap = 7,
}
