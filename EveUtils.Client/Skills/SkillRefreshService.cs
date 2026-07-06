using System;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Esi.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EveUtils.Client.Skills;

/// <summary>
/// Keeps every coupled character's imported skills fresh: imports the skills snapshot + skill-queue for all
/// registered characters on start, then re-imports on a short interval so a long-running session reflects training
/// progress (a finished skill, a changed queue). Both ESI skill endpoints (<c>/characters/{id}/skills/</c> and
/// <c>/characters/{id}/skillqueue/</c>) carry a 120 s cache timer (<c>x-cached-seconds</c>, ESI swagger) and the ESI
/// pipeline honours that TTL, so the interval is anchored to it: polling faster only yields cache hits, slower lets the
/// data age. Characters that have not granted the two skill scopes (read_skills/read_skillqueue) are skipped quietly —
/// the background never raises a re-auth prompt. A newly added character is imported immediately via
/// <see cref="ICharacterRegistry.RegistryChanged"/>.
/// </summary>
public sealed class SkillRefreshService(
    IEsiSkillImporter importer,
    ICharacterRegistry registry,
    IEsiAvailabilityState availability,
    ILogger<SkillRefreshService> logger) : BackgroundService
{
    // /skills/ and /skillqueue/ both cache for 120 s (ESI swagger x-cached-seconds); the pipeline honours that TTL,
    // so this is the shortest poll that returns fresh data rather than a cache hit.
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(120);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        registry.RegistryChanged += _OnRegistryChanged;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await RefreshAllAsync(stoppingToken);

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

    // A newly coupled character should import its skills straight away, not only at the next tick.
    private void _OnRegistryChanged() => _ = RefreshAllAsync(CancellationToken.None);

    /// <summary>Imports skills for every registered character once, unless ESI is down. Public for testing.</summary>
    public async Task RefreshAllAsync(CancellationToken cancellationToken)
    {
        // ESI is down (failed /status/ poll) — the gate would withhold every call anyway, so skip the whole cycle
        // instead of firing per-character imports that just come back withheld.
        if (!availability.IsUsable)
        {
            logger.LogDebug("ESI unavailable — skipping skill refresh this cycle.");
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
                    case SkillImportStatus.ScopeMissing:
                    case SkillImportStatus.AuthRequired:
                        // Expected for characters that never granted the skill scopes — skip quietly.
                        logger.LogDebug("Skipped skill refresh for character {CharacterId}: {Status}.", characterId, result.Status);
                        break;
                    case SkillImportStatus.Failed:
                        logger.LogWarning("Skill refresh failed for character {CharacterId}: {Message}", characterId, result.Message);
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
            logger.LogError(ex, "Failed to refresh character skills.");
        }
    }
}
