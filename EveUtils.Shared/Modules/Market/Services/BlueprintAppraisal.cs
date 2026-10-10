namespace EveUtils.Shared.Modules.Market.Services;

/// <summary>One input of a blueprint build: what the runs need after ME, and what one unit costs at the EVE average.</summary>
public sealed record BlueprintMaterialCost(int TypeId, long BaseQuantityPerRun, long Needed, decimal? UnitPrice)
{
    public decimal? Total => UnitPrice * Needed;
}

/// <summary>
/// Building a number of runs of one blueprint, valued at the EVE average prices (ET-501). A figure that needs an average
/// price is null as soon as one is missing: a build valued in part reads as an answer it is not.
/// </summary>
public sealed record BlueprintAppraisal(
    int BlueprintTypeId, int Runs, int MaterialEfficiency, int ProductTypeId, long ProductQuantity, decimal? ProductUnitPrice,
    IReadOnlyList<BlueprintMaterialCost> Materials, decimal EstimatedItemValue, decimal JobCost)
{
    public bool IsComplete => ProductUnitPrice is not null && Materials.All(material => material.UnitPrice is not null);

    public decimal? ProductValue => IsComplete ? ProductUnitPrice * ProductQuantity : null;

    public decimal? MaterialsCost => IsComplete ? Materials.Sum(material => material.Total ?? 0m) : null;

    public decimal? Profit => ProductValue - MaterialsCost - JobCost;

    /// <summary>What the build is worth as something owned: its profit, floored at 0 — a blueprint that loses money to
    /// build is worth nothing, never less.</summary>
    public decimal? Value => Profit is { } profit ? Math.Max(0m, profit) : null;
}
