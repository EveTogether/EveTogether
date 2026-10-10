using EveUtils.Shared.Modules.Sde.Dtos;

namespace EveUtils.Shared.Modules.Market.Services;

/// <summary>
/// The arithmetic of a manufacturing job (ET-501), free of storage so it can be checked against known numbers.
///
/// Materials: <c>needed = max(runs, ceil(round(runs × base × (1 − ME/100), 2)))</c> — the rounding to two decimals
/// removes float noise before the ceiling, and a job never needs less than one unit per run.
///
/// Job cost: <c>EIV × <see cref="JobCostRate"/></c>, where the estimated item value is the base quantities × runs ×
/// CCP's adjusted price; ME does not change it. A material without an adjusted price adds nothing to it.
/// </summary>
public static class BlueprintBuildCalculator
{
    /// <summary>A system cost index of 5%, the 4% SCC surcharge and no facility tax. ET keeps no industry cost indices,
    /// so every job is costed at this fixed share of its estimated item value.</summary>
    public const decimal JobCostRate = 0.09m;

    public const int MaxMaterialEfficiency = 10;

    public static long RequiredQuantity(long baseQuantity, int runs, int materialEfficiency)
    {
        runs = Math.Max(1, runs);
        materialEfficiency = Math.Clamp(materialEfficiency, 0, MaxMaterialEfficiency);
        decimal raw = runs * baseQuantity * (1m - materialEfficiency / 100m);
        long needed = (long)Math.Ceiling(Math.Round(raw, 2, MidpointRounding.AwayFromZero));
        return Math.Max(runs, needed);
    }

    public static BlueprintAppraisal Appraise(SdeBlueprintManufacturing blueprint, int runs, int materialEfficiency,
        IReadOnlyDictionary<int, double> averagePrices, IReadOnlyDictionary<int, double> adjustedPrices)
    {
        runs = Math.Max(1, runs);
        materialEfficiency = Math.Clamp(materialEfficiency, 0, MaxMaterialEfficiency);
        BlueprintMaterialCost[] materials = [.. blueprint.Materials.Select(material => new BlueprintMaterialCost(
            material.TypeId, material.Quantity, RequiredQuantity(material.Quantity, runs, materialEfficiency),
            _Price(averagePrices, material.TypeId)))];
        decimal estimatedItemValue = blueprint.Materials.Sum(material =>
            (decimal)material.Quantity * runs * (_Price(adjustedPrices, material.TypeId) ?? 0m));
        return new BlueprintAppraisal(blueprint.BlueprintTypeId, runs, materialEfficiency, blueprint.ProductTypeId,
            (long)blueprint.ProductQuantity * runs, _Price(averagePrices, blueprint.ProductTypeId), materials,
            estimatedItemValue, estimatedItemValue * JobCostRate);
    }

    private static decimal? _Price(IReadOnlyDictionary<int, double> prices, int typeId) =>
        prices.TryGetValue(typeId, out double price) ? (decimal)price : null;
}
