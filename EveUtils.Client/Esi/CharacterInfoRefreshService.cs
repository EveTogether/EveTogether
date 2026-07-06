using System;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Esi.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EveUtils.Client.Esi;

/// <summary>
/// Keeps every known character's public affiliation fresh: resolves corp/alliance for all registered characters
/// on start, then re-checks hourly so a long-running session still reflects a corp/name change. Resolution goes
/// through <see cref="ICharacterInfoService"/> (the metered ESI pipeline) whose file cache honours ESI's TTLs
/// (character ~1 day, corporation ~1 hour), so an in-window refresh is a cheap cache hit. A newly added character
/// is resolved immediately via <see cref="ICharacterRegistry.RegistryChanged"/>.
/// </summary>
public sealed class CharacterInfoRefreshService(
    ICharacterInfoService characterInfo,
    ICharacterRegistry registry,
    IEsiAvailabilityState availability,
    ILogger<CharacterInfoRefreshService> logger) : BackgroundService
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(1);

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

    // A new pairing should show its corp/alliance straight away, not only at the next hourly tick.
    private void _OnRegistryChanged() => _ = _RefreshAllAsync(CancellationToken.None);

    private async Task _RefreshAllAsync(CancellationToken cancellationToken)
    {
        // ESI is down (failed /status/ poll) — the gate would withhold every call anyway, so skip the whole cycle.
        if (!availability.IsUsable)
        {
            logger.LogDebug("ESI unavailable — skipping affiliation refresh this cycle.");
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
                if (characterId > 0)
                    await characterInfo.RefreshAsync(characterId, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // shutting down — nothing to do
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to refresh character affiliations.");
        }
    }
}
