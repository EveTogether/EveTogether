namespace EveUtils.Shared.Modules.Runs.Enums;

/// <summary>What a loot line's fixed unit price was read from (ET-501). Kept beside the price, because a blueprint that
/// also has a market average could have been valued either way, and only the moment of pricing knows which.</summary>
public enum LootPriceBasis
{
    /// <summary>The EVE average market price of the type itself.</summary>
    Market,

    /// <summary>A build appraisal of the blueprint: product minus materials minus job cost, at EVE average prices.</summary>
    BlueprintAppraisal
}
