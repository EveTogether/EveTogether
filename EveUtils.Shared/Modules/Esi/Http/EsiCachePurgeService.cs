using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EveUtils.Shared.Modules.Esi.Http;

/// <summary>
/// Periodically drops expired ESI cache entries, in the style of <c>ServerSessionCleanupService</c>.
/// Hosted on the server; started manually on the client (which has no generic host).
/// </summary>
public sealed class EsiCachePurgeService(IEsiCacheStore store, ILogger<EsiCachePurgeService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(Interval, stoppingToken);
            try
            {
                var purged = await store.PurgeExpiredAsync(stoppingToken);
                if (purged > 0)
                    logger.LogInformation("Purged {Count} expired ESI cache entries.", purged);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "ESI cache purge cycle failed.");
            }
        }
    }
}
