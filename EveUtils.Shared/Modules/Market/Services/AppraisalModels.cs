namespace EveUtils.Shared.Modules.Market.Services;

/// <summary>One item to be valued: how many of which type, with the name kept beside the id so a provider that
/// speaks names rather than ids has one, and so the result can be shown without a second SDE lookup.</summary>
public sealed record AppraisalLine(int TypeId, string Name, long Quantity);

/// <summary>
/// What one unit is worth. <paramref name="Estimate"/> is the figure to show and to total with, and is the only one
/// every provider can fill. <paramref name="Buy"/> and <paramref name="Sell"/> are the market's two sides, which a
/// provider quoting a real order book fills and one quoting a single average leaves empty.
/// </summary>
public sealed record AppraisalPrice(double Estimate, double? Buy = null, double? Sell = null);

/// <summary>A valued line. <paramref name="Price"/> is null when the type is known but carries no price.</summary>
public sealed record AppraisalRow(AppraisalLine Line, AppraisalPrice? Price);

/// <summary>
/// The valuation of a whole list. <paramref name="Unresolved"/> holds the names a provider that resolves names
/// itself could not read — a provider handed type ids leaves it empty and its caller reports what it could not
/// resolve. <paramref name="PricingBasis"/> says in one sentence what these prices are and when they are from, so
/// the screen never has to know which provider produced them.
/// </summary>
public sealed record AppraisalOutcome(
    IReadOnlyList<AppraisalRow> Rows,
    IReadOnlyList<string> Unresolved,
    string PricingBasis)
{
    /// <summary>The list's total value: every row's estimate times its quantity, priceless rows counting as nothing.</summary>
    public double Total => Rows.Sum(row => (row.Price?.Estimate ?? 0) * row.Line.Quantity);
}
