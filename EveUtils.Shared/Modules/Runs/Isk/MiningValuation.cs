using EveUtils.Shared.Modules.Sde;

namespace EveUtils.Shared.Modules.Runs.Isk;

/// <summary>How one ore is priced (ET-229). Mutanite is the one documented exception — a fixed NPC buy price, never
/// the market's — kept here, visible and named, rather than folded silently into a general pricing rule.</summary>
public static class MiningValuation
{
    /// <summary>SDE group "Mutanite" (4568): Amperum, Peregrinus, Conflagrati, Solis, Tenebraet and Admixti Mutanite
    /// — six types, one per homefront variant. Measured against the SDE (domain research, homefronts.md §6.4):
    /// checking the group rather than naming the six types by hand also covers a build that adds a seventh.</summary>
    public const int MutaniteGroupId = 4568;

    /// <summary>NPC buy price per unit, wiki-sourced (homefronts.md §3.4/§6.4) — not the market's, which can be thin
    /// or absent for an ore only ever sold to an NPC. Whether a corp actually sells to the NPC or the market is
    /// still open (ET-227 V11); until then this is the one figure that matches what Jithran has measured.</summary>
    public const decimal MutaniteNpcBuyPricePerUnit = 5_000m;

    /// <summary>The unit price for a resolved ore type, or null when neither the fixed exception nor the market
    /// cache has one.</summary>
    public static decimal? UnitPrice(ISdeAccessor sde, int typeId, IReadOnlyDictionary<int, double> marketPrices) =>
        sde.IsAvailable && sde.GetType(typeId) is { } type && type.GroupId == MutaniteGroupId
            ? MutaniteNpcBuyPricePerUnit
            : marketPrices.TryGetValue(typeId, out double price) ? (decimal)price : null;
}
