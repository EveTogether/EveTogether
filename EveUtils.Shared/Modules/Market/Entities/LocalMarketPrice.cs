namespace EveUtils.Shared.Modules.Market.Entities;

/// <summary>
/// A cached ESI market price for a type. The client refreshes these hourly from the public
/// <c>GET /markets/prices/</c> endpoint and uses the average price to estimate a fit's ISK value. Client-local
/// (the <c>Local*</c> convention), so the entity name doubles as the table name.
/// </summary>
public sealed class LocalMarketPrice
{
    public int TypeId { get; set; }
    public double AveragePrice { get; set; }
    public double AdjustedPrice { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
