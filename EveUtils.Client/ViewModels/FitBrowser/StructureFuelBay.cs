namespace EveUtils.Client.ViewModels.FitBrowser;

/// <summary>
/// Upwell structure fuel-bay capacity for the fit-detail runtime estimate. The bay capacity is not in the SDE (attr 1549
/// "Fuel Bay Capacity" sits only on ships with a jump drive, never on a category-65 structure — confirmed against SDE,
/// EVERef, ESI and Fuzzwork, research 2026-06-07), so it is hard-coded here. CCP exposes a single, class-independent bay
/// of 5,000,000 m³ for every Upwell structure, shown only in-game. PROVISIONAL: single-source community value, pending
/// in-game confirmation (Show Info → Attributes).
/// </summary>
public static class StructureFuelBay
{
    /// <summary>The fixed Upwell structure fuel bay in m³ (provisional, pending in-game confirmation).</summary>
    public const double FuelBayCapacityM3 = 5_000_000;

    /// <summary>Every fuel block (SDE group 1136) is 5 m³ — SDE-confirmed.</summary>
    public const double FuelBlockVolumeM3 = 5;

    /// <summary>The structure fuel bay expressed in fuel blocks (capacity m³ / block volume).</summary>
    public const double CapacityInBlocks = FuelBayCapacityM3 / FuelBlockVolumeM3;
}
