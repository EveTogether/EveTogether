using System;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Esi.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EveUtils.Client.Killmails;

/// <summary>
/// Imports every coupled character's new kills and losses on start and then every 5 minutes, the cache time of
/// <c>/characters/{id}/killmails/recent/</c>. Characters without the killmail scope are skipped quietly.
/// </summary>
public sealed class KillmailRefreshService(
    EsiKillmailImporter importer,
    ICharacterRegistry registry,
    IEsiAvailabilityState availability,
    ILogger<KillmailRefreshService> logger) : BackgroundService
{
    // The recent list caches for 300 s and its rate group allows 30 tokens per 15 minutes; faster polling buys nothing.
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await _RefreshAllAsync(stoppingToken);

            try
            {
                await Task.Delay(RefreshInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    // Imports killmails for every registered character once, unless ESI is down.
    private async Task _RefreshAllAsync(CancellationToken cancellationToken)
    {
        if (!availability.IsUsable)
        {
            logger.LogDebug("ESI unavailable — skipping killmail refresh this cycle.");
            return;
        }

        try
        {
            var characters = await registry.GetAllAsync(cancellationToken);
            foreach (var character in characters)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                var characterId = character.EsiCharacterId ?? 0;
                if (characterId <= 0)
                {
                    continue;
                }

                var result = await importer.ImportAsync(characterId, cancellationToken);
                switch (result.Status)
                {
                    case KillmailImportStatus.ScopeMissing:
                    case KillmailImportStatus.AuthRequired:
                        logger.LogDebug("Skipped killmail refresh for character {CharacterId}: {Status}.", characterId, result.Status);
                        break;
                    case KillmailImportStatus.Failed:
                        logger.LogWarning("Killmail refresh failed for character {CharacterId}: {Message}", characterId, result.Message);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // shutting down — nothing to do
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to refresh character killmails.");
        }
    }
}
