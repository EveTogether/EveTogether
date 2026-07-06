using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Shared.Modules.Esi.Http;
using EveUtils.Shared.Modules.Market.Entities;
using EveUtils.Shared.Modules.Market.Repositories;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EveUtils.Client.Esi;

/// <summary>
/// Refreshes the local market-price cache from the public ESI <c>GET /markets/prices/</c> endpoint once an hour
/// — the endpoint's cache TTL — and stores the snapshot in SQLite so the fit-detail window can estimate a fit's ISK
/// value offline. Public call (no token), so it works with no characters signed in. Failures are logged and retried
/// next cycle; the existing cache stays in place.
/// </summary>
public sealed class EsiMarketPriceService(
    IEsiClient esi,
    IMarketPriceRepository repository,
    ILogger<EsiMarketPriceService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromHours(1);
    private const string PricesPath = "/markets/prices/";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to refresh ESI market prices.");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    /// <summary>Fetches the full price list and replaces the cache; a no-op when the call fails or returns nothing.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var result = await esi.GetAsync<EsiMarketPrice[]>(PricesPath, cancellationToken: cancellationToken);
        if (!result.IsSuccess || result.Value is null)
            return;

        var now = DateTimeOffset.UtcNow;
        var prices = result.Value
            .Where(price => price.AveragePrice > 0 || price.AdjustedPrice > 0)
            .Select(price => new LocalMarketPrice
            {
                TypeId = price.TypeId,
                AveragePrice = price.AveragePrice,
                AdjustedPrice = price.AdjustedPrice,
                UpdatedAt = now
            })
            .ToList();
        if (prices.Count == 0)
            return;

        await repository.ReplaceAllAsync(prices, cancellationToken);
        logger.LogInformation("Refreshed {Count} ESI market prices.", prices.Count);
    }
}
