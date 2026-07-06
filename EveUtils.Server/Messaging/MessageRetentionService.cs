using EveUtils.Shared.Modules.Messaging.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EveUtils.Server.Messaging;

/// <summary>
/// Periodically purges expired queued messages so the message queue stays a ~30-day transport buffer
/// rather than an unbounded log. Mirrors <c>ServerSessionCleanupService</c>. Runs every 30 minutes
/// (plus once shortly after startup).
/// </summary>
public sealed class MessageRetentionService(
    IServiceScopeFactory scopeFactory,
    ILogger<MessageRetentionService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var repository = scope.ServiceProvider.GetRequiredService<IMessageRepository>();
                var removed = await repository.DeleteExpiredAsync(DateTimeOffset.UtcNow, stoppingToken);
                if (removed > 0)
                    logger.LogInformation("Purged {Count} expired message(s).", removed);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Message retention cleanup failed.");
            }

            await Task.Delay(Interval, stoppingToken);
        }
    }
}
