using System.Collections.Concurrent;
using System.Globalization;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Esi;
using EveUtils.Shared.Modules.Esi.Http;
using EveUtils.Shared.Modules.Fittings.Entities;
using EveUtils.Shared.Modules.Fittings.Repositories;
using EveUtils.Shared.Modules.Location;
using EveUtils.Shared.Modules.Settings.Entities;
using EveUtils.Shared.Modules.Settings.Repositories;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EveUtils.Client.Esi;

/// <summary>Polls each eligible character's active ship every 30 seconds and exposes the latest in-memory reading.</summary>
public sealed class ShipFitDetectionService(
    IEsiCharacterShipClient ships,
    ICharacterRegistry registry,
    IFittingRepository fittings,
    ISettingRepository settings,
    IEsiAvailabilityState availability,
    IEsiRateLimitMonitor rateLimits,
    ILogger<ShipFitDetectionService> logger) : BackgroundService, IShipFitDetectionService
{
    private const string OverrideKeyPrefix = "fit-detection.override.";
    // Stored where a fitting id goes, and no fitting has it: "the player chose no fit" is a choice, not the absence
    // of one, and it has to outlive the window that made it.
    private const int DetachedFitId = 0;
    // Provisional until a scoped ship response confirms this bucket's live headroom; low-headroom cycles yield instead of queueing.
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<int, ShipFitDetectionReading> _readings = new();
    private readonly ConcurrentDictionary<(int CharacterId, int ShipTypeId), int> _manualFits = new();
    private readonly Lock _overrideLoadGate = new();
    private Task? _loadOverridesTask;

    public ShipFitDetectionReading GetReading(int characterId) =>
        _readings.TryGetValue(characterId, out ShipFitDetectionReading? reading) ? reading : ShipFitDetectionReading.Unobserved;

    public async Task<Result> SetManualFitAsync(int characterId, int? fittingId, CancellationToken cancellationToken = default)
    {
        if (fittingId is { } id)
        {
            LocalFitting? fitting = await fittings.FindByIdAsync(id, cancellationToken);
            if (fitting is null)
                return Result.Failure(new ResultMessage(
                    MessageSeverity.Error, "FITTING_NOT_FOUND", "The selected fitting no longer exists."));

            _manualFits[(characterId, fitting.ShipTypeId)] = id;
            await settings.UpsertAsync(_OverrideKey(characterId, fitting.ShipTypeId), id.ToString(CultureInfo.InvariantCulture), cancellationToken);
        }
        else
        {
            await _ClearManualFitsAsync(characterId, cancellationToken);
        }

        _Reselect(characterId);
        return Result.Success();
    }

    public async Task<Result> DetachFitAsync(int characterId, CancellationToken cancellationToken = default)
    {
        if (!_readings.TryGetValue(characterId, out ShipFitDetectionReading? reading) || reading.ShipTypeId is not { } shipTypeId)
            return Result.Failure(new ResultMessage(
                MessageSeverity.Error, "SHIP_UNKNOWN", "The current ship is not known yet, so there is no fit to unlink."));

        _manualFits[(characterId, shipTypeId)] = DetachedFitId;
        await settings.UpsertAsync(_OverrideKey(characterId, shipTypeId),
            DetachedFitId.ToString(CultureInfo.InvariantCulture), cancellationToken);
        _Reselect(characterId);
        return Result.Success();
    }

    /// <summary>Re-derive the stored reading against the manual fits as they now stand. Without this the choice only
    /// shows on the next 30-second poll, and a caller that renders the reading would keep showing the old fit.</summary>
    private void _Reselect(int characterId)
    {
        if (!_readings.TryGetValue(characterId, out ShipFitDetectionReading? reading) || reading.ShipTypeId is not { } shipTypeId)
            return;

        (ShipFitCandidate? selected, ShipFitMatchReason reason) =
            _Select(characterId, shipTypeId, reading.ShipName, reading.Candidates);
        _readings[characterId] = reading with { SelectedFit = selected, MatchReason = reason };
    }

    private (ShipFitCandidate? Selected, ShipFitMatchReason Reason) _Select(
        int characterId, int shipTypeId, string? shipName, IReadOnlyList<ShipFitCandidate> candidates)
    {
        // A manual fit is stored under the ship type it belongs to, so a hit here is always for the observed ship.
        if (_manualFits.TryGetValue((characterId, shipTypeId), out int manualFitId))
        {
            if (manualFitId == DetachedFitId)
                return (null, ShipFitMatchReason.Detached);

            ShipFitCandidate? manual = candidates.FirstOrDefault(candidate => candidate.Id == manualFitId);
            return (manual, manual is null ? ShipFitMatchReason.NoFitFound : ShipFitMatchReason.Manual);
        }

        ShipFitCandidate[] nameMatches = candidates
            .Where(candidate => string.Equals(candidate.Name, shipName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (nameMatches.Length == 1)
            return (nameMatches[0], ShipFitMatchReason.ShipName);
        if (candidates.Count == 0)
            return (null, ShipFitMatchReason.NoFitFound);
        if (candidates.Count == 1)
            return (candidates[0], ShipFitMatchReason.OnlyFitForShipType);

        return (null, ShipFitMatchReason.AmbiguousShipType);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        registry.RegistryChanged += _OnRegistryChanged;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await RefreshAllAsync(stoppingToken);
                await Task.Delay(PollInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            registry.RegistryChanged -= _OnRegistryChanged;
        }
    }

    private void _OnRegistryChanged() => _ = RefreshAllAsync(CancellationToken.None);

    internal async Task RefreshAllAsync(CancellationToken cancellationToken)
    {
        await _EnsureOverridesLoadedAsync(cancellationToken);
        if (!availability.IsUsable)
            return;

        IReadOnlyList<Character> characters = await registry.GetAllAsync(cancellationToken);
        foreach (Character character in characters)
        {
            if (cancellationToken.IsCancellationRequested)
                return;

            if (character.EsiCharacterId is not { } characterId)
                continue;

            if (!character.HasScope(LocationScopeCatalog.ReadShipType))
            {
                _readings[characterId] = ShipFitDetectionReading.ScopeMissing;
                continue;
            }

            if (rateLimits.GetBucket($"app:{characterId}")?.ShouldYieldNonEssentialCall(DateTimeOffset.UtcNow) is true)
            {
                logger.LogDebug("Skipping current-ship poll for {CharacterId}: ESI bucket has low headroom.", characterId);
                continue;
            }

            await RefreshCharacterAsync(characterId, cancellationToken);
        }
    }

    internal async Task RefreshCharacterAsync(int characterId, CancellationToken cancellationToken)
    {
        EsiResult<EsiCharacterShip> result = await ships.GetShipAsync(characterId, cancellationToken);
        if (!result.IsSuccess || result.Value is not { } ship)
        {
            if (result.Error?.Kind == EsiErrorKind.ScopeMissing)
                _readings[characterId] = ShipFitDetectionReading.ScopeMissing;
            else
                logger.LogWarning("Could not read current ship for {CharacterId}: {ErrorCode}", characterId, result.Error?.Code);
            return;
        }

        IReadOnlyList<LocalFitting> knownFits = await fittings.ListAllAsync(cancellationToken);
        ShipFitCandidate[] candidates = knownFits
            .Where(fitting => fitting.ShipTypeId == ship.ShipTypeId)
            .Select(fitting => new ShipFitCandidate(fitting.Id, fitting.Name, fitting.ShipTypeId))
            .ToArray();

        (ShipFitCandidate? selected, ShipFitMatchReason reason) =
            _Select(characterId, ship.ShipTypeId, ship.ShipName, candidates);

        _readings[characterId] = new ShipFitDetectionReading(
            ShipFitDetectionState.Observed,
            DateTimeOffset.UtcNow,
            ship.ShipTypeId,
            ship.ShipItemId,
            ship.ShipName,
            selected,
            reason,
            candidates);
    }

    private async Task _EnsureOverridesLoadedAsync(CancellationToken cancellationToken)
    {
        Task loadTask;
        lock (_overrideLoadGate)
            loadTask = _loadOverridesTask ??= _LoadOverridesAsync(cancellationToken);
        await loadTask;
    }

    private async Task _LoadOverridesAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<ClientSetting> stored = await settings.ListAsync(cancellationToken);
        foreach (ClientSetting setting in stored)
        {
            string[] keyParts = setting.Key.StartsWith(OverrideKeyPrefix, StringComparison.Ordinal)
                ? setting.Key[OverrideKeyPrefix.Length..].Split('.')
                : [];
            if (keyParts.Length != 2 ||
                !int.TryParse(keyParts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int characterId) ||
                !int.TryParse(keyParts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int shipTypeId) ||
                !int.TryParse(setting.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int fittingId))
                continue;

            _manualFits[(characterId, shipTypeId)] = fittingId;
        }
    }

    private async Task _ClearManualFitsAsync(int characterId, CancellationToken cancellationToken)
    {
        foreach ((int CharacterId, int ShipTypeId) key in _manualFits.Keys.Where(key => key.CharacterId == characterId))
            _manualFits.TryRemove(key, out _);

        IReadOnlyList<ClientSetting> stored = await settings.ListAsync(cancellationToken);
        foreach (ClientSetting setting in stored.Where(setting => setting.Key.StartsWith(_OverrideKeyPrefix(characterId), StringComparison.Ordinal)))
            await settings.UpsertAsync(setting.Key, string.Empty, cancellationToken);
    }

    private static string _OverrideKeyPrefix(int characterId) =>
        OverrideKeyPrefix + characterId.ToString(CultureInfo.InvariantCulture) + ".";

    private static string _OverrideKey(int characterId, int shipTypeId) =>
        _OverrideKeyPrefix(characterId) + shipTypeId.ToString(CultureInfo.InvariantCulture);
}
