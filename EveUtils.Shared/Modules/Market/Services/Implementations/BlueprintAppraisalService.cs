using EveUtils.Shared.Cqrs;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Market.Repositories;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Sde.Dtos;
using EveUtils.Shared.Modules.Settings.Queries;

namespace EveUtils.Shared.Modules.Market.Services.Implementations;

/// <summary>See <see cref="IBlueprintAppraisalService"/>.</summary>
public sealed class BlueprintAppraisalService(ISdeAccessor sde, IMarketPriceRepository marketPrices, IDispatcher dispatcher)
    : IBlueprintAppraisalService, IScopedService
{
    /// <summary>Whether blueprints in loot are valued with a build appraisal. Absent = on; "false" = off.</summary>
    public const string LootValuationSettingKey = "loot.value-blueprints";

    public async Task<BlueprintAppraisal?> AppraiseAsync(int blueprintTypeId, int runs, int materialEfficiency,
        CancellationToken cancellationToken = default)
    {
        if (sde.GetBlueprintManufacturing(blueprintTypeId) is not { } blueprint)
            return null;

        IReadOnlyList<BlueprintAppraisal> appraised = await _AppraiseAsync([blueprint], runs, materialEfficiency, cancellationToken);
        return appraised[0];
    }

    public async Task<IReadOnlyDictionary<int, decimal?>> GetLootValuesAsync(IReadOnlyCollection<int> typeIds,
        CancellationToken cancellationToken = default)
    {
        if (typeIds.Count == 0 || !await _IsLootValuationOnAsync(cancellationToken))
            return new Dictionary<int, decimal?>();

        SdeBlueprintManufacturing[] blueprints = [.. typeIds
            .Distinct()
            .Select(sde.GetBlueprintManufacturing)
            .OfType<SdeBlueprintManufacturing>()];
        if (blueprints.Length == 0)
            return new Dictionary<int, decimal?>();

        IReadOnlyList<BlueprintAppraisal> appraised = await _AppraiseAsync(blueprints, runs: 1, materialEfficiency: 0, cancellationToken);
        return appraised.ToDictionary(appraisal => appraisal.BlueprintTypeId, appraisal => appraisal.Value);
    }

    private async Task<IReadOnlyList<BlueprintAppraisal>> _AppraiseAsync(IReadOnlyCollection<SdeBlueprintManufacturing> blueprints,
        int runs, int materialEfficiency, CancellationToken cancellationToken)
    {
        int[] materialIds = [.. blueprints.SelectMany(blueprint => blueprint.Materials.Select(material => material.TypeId)).Distinct()];
        int[] typeIds = [.. materialIds.Concat(blueprints.Select(blueprint => blueprint.ProductTypeId)).Distinct()];
        IReadOnlyDictionary<int, double> averages = await marketPrices.GetAveragePricesAsync(typeIds, cancellationToken);
        IReadOnlyDictionary<int, double> adjusted = await marketPrices.GetAdjustedPricesAsync(materialIds, cancellationToken);
        return [.. blueprints.Select(blueprint =>
            BlueprintBuildCalculator.Appraise(blueprint, runs, materialEfficiency, averages, adjusted))];
    }

    private async Task<bool> _IsLootValuationOnAsync(CancellationToken cancellationToken)
    {
        var settings = await dispatcher.Query(new GetSettingsQuery(), cancellationToken);
        return settings.FirstOrDefault(setting => setting.Key == LootValuationSettingKey)?.Value != "false";
    }
}
