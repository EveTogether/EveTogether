namespace EveUtils.Shared.Modules.Fleet;

/// <summary>
/// In-game EVE fleet structure limits (game-design caps). The ESI spec deliberately does not document these,
/// so we keep our own constants (sourced from EVE University documentation). Open-for-extension
/// a future per-fleet override can replace these without touching the validating handlers.
/// </summary>
public static class FleetStructureLimits
{
    public const int MaxWingsPerFleet = 5;

    public const int MaxSquadsPerWing = 5;

    /// <summary>Members per squad, the squad commander included (EVE: 9 members + SC).</summary>
    public const int MaxMembersPerSquad = 10;

    /// <summary>Hard cap on a whole fleet (5 wings × 5 squads × 10 + 5 WC + 1 FC).</summary>
    public const int MaxFleetSize = 256;
}
