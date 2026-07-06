using System;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Esi.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EveUtils.Client.Implants;

/// <summary>
/// Keeps every coupled character's imported implants fresh: imports on start, then re-imports on a short
/// interval so a long-running session reflects an implant swap. The cadence is pinned at 120 s. Characters that
/// have not granted the clones scope (esi-clones.read_implants) are skipped quietly — the background never raises a
/// re-auth prompt. A newly added character is imported immediately via <see cref="ICharacterRegistry.RegistryChanged"/>.
/// </summary>
public sealed class ImplantRefreshService(
    IEsiImplantImporter importer,
    ICharacterRegistry registry,
    IEsiAvailabilityState availability,
    ILogger<ImplantRefreshService> logger) : BackgroundService
{
    // Pinned 120 s cadence (matches the skill refresh). The ESI pipeline honours the /implants/ cache TTL, so
    // polling faster than that TTL only yields cheap cache hits rather than fresh fetches.
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(120);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        registry.RegistryChanged += _OnRegistryChanged;
        try
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
        finally
        {
            registry.RegistryChanged -= _OnRegistryChanged;
        }
    }

    // A newly coupled character should import its implants straight away, not only at the next tick.
    private void _OnRegistryChanged() => _ = _RefreshAllAsync(CancellationToken.None);

    private async Task _RefreshAllAsync(CancellationToken cancellationToken)
    {
        // ESI is down (failed /status/ poll) — the gate would withhold every call anyway, so skip the whole cycle
        // instead of firing per-character imports that just come back withheld.
        if (!availability.IsUsable)
        {
            logger.LogDebug("ESI unavailable — skipping implant refresh this cycle.");
            return;
        }

        try
        {
            var characters = await registry.GetAllAsync(cancellationToken);
            foreach (var character in characters)
            {
                if (cancellationToken.IsCancellationRequested)
                    return;

                var characterId = character.EsiCharacterId ?? 0;
                if (characterId <= 0)
                    continue;

                var result = await importer.ImportAsync(characterId, cancellationToken);
                switch (result.Status)
                {
                    case ImplantImportStatus.ScopeMissing:
                    case ImplantImportStatus.AuthRequired:
                        // Expected for characters that never granted the clones scope — skip quietly.
                        logger.LogDebug("Skipped implant refresh for character {CharacterId}: {Status}.", characterId, result.Status);
                        break;
                    case ImplantImportStatus.Failed:
                        logger.LogWarning("Implant refresh failed for character {CharacterId}: {Message}", characterId, result.Message);
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
            logger.LogError(ex, "Failed to refresh character implants.");
        }
    }
}
