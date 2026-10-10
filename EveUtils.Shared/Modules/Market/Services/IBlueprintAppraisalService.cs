namespace EveUtils.Shared.Modules.Market.Services;

/// <summary>Values building a blueprint at the EVE average prices this client keeps (ET-501) — the one calculation
/// both the loot valuation and the appraisal tool use.</summary>
public interface IBlueprintAppraisalService
{
    /// <summary>Building <paramref name="runs"/> runs at <paramref name="materialEfficiency"/>, or null when the SDE
    /// knows no manufacturing for the type.</summary>
    Task<BlueprintAppraisal?> AppraiseAsync(int blueprintTypeId, int runs, int materialEfficiency,
        CancellationToken cancellationToken = default);

    /// <summary>What one of each blueprint among <paramref name="typeIds"/> is worth as loot: one run at ME 0, since a
    /// loot line carries neither its runs nor its ME, floored at 0. A blueprint whose build cannot be priced in full maps
    /// to null; a type that is no blueprint is absent. Empty while the user has turned blueprint valuation off.</summary>
    Task<IReadOnlyDictionary<int, decimal?>> GetLootValuesAsync(IReadOnlyCollection<int> typeIds,
        CancellationToken cancellationToken = default);
}
