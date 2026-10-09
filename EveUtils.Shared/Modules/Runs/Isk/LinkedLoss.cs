using EveUtils.Shared.Modules.Killmails.Entities;
using EveUtils.Shared.Modules.Runs.Entities;

namespace EveUtils.Shared.Modules.Runs.Isk;

/// <summary>A loss linked to a run (ET-331) with the unit prices the run fixed for it when it was linked (ET-464). A
/// type without a fixed price is valued at the live cache price.</summary>
internal sealed class LinkedLoss(LocalKillmail killmail, IReadOnlyDictionary<int, decimal> fixedPrices)
{
    public LocalKillmail Killmail { get; } = killmail;

    public static LinkedLoss Of(LocalKillmail killmail, IEnumerable<RunLossPrice> prices) =>
        new(killmail, prices
            .Where(price => price.CharacterId == killmail.CharacterId && price.KillmailId == killmail.KillmailId
                            && price.UnitPriceIsk is not null)
            .ToDictionary(price => price.TypeId, price => price.UnitPriceIsk.GetValueOrDefault()));

    /// <summary>The hull and every item, each type once: the lines a link fixes a price for.</summary>
    public static IEnumerable<int> TypeIds(LocalKillmail killmail) =>
        killmail.Items.Select(item => item.TypeId).Prepend(killmail.VictimShipTypeId).Distinct();

    public bool HasLivePrice => TypeIds(Killmail).Any(typeId => !fixedPrices.ContainsKey(typeId));

    public decimal? UnitPrice(int typeId, IReadOnlyDictionary<int, double> live) =>
        fixedPrices.TryGetValue(typeId, out decimal price) ? price
        : live.TryGetValue(typeId, out double livePrice) ? (decimal)livePrice
        : null;
}
