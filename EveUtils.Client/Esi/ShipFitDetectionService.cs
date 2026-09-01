using System.Collections.Concurrent;
using System.Globalization;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
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
    ILogger<ShipFitDetectionService> logger) : BackgroundService, IShipFitDetectionService
{
    private const string OverrideKeyPrefix = "fit-detection.override.";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<int, ShipFitDetectionReading> _readings = new();
    private readonly ConcurrentDictionary<int, int> _manualFits = new();
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

            _manualFits[characterId] = id;
            await settings.UpsertAsync(_OverrideKey(characterId), id.ToString(CultureInfo.InvariantCulture), cancellationToken);
        }
        else
        {
            _manualFits.TryRemove(characterId, out _);
            await settings.UpsertAsync(_OverrideKey(characterId), string.Empty, cancellationToken);
        }

        return Result.Success();
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

        ShipFitCandidate? selected = null;
        ShipFitMatchReason reason;
        if (_manualFits.TryGetValue(characterId, out int manualFitId))
        {
            selected = knownFits.Where(fitting => fitting.Id == manualFitId)
                .Select(fitting => new ShipFitCandidate(fitting.Id, fitting.Name, fitting.ShipTypeId))
                .FirstOrDefault();
            reason = selected is null ? ShipFitMatchReason.NoFitFound : ShipFitMatchReason.Manual;
        }
        else
        {
            ShipFitCandidate[] nameMatches = candidates
                .Where(fitting => string.Equals(fitting.Name, ship.ShipName, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (nameMatches.Length == 1)
                (selected, reason) = (nameMatches[0], ShipFitMatchReason.ShipName);
            else if (candidates.Length == 0)
                (selected, reason) = (null, ShipFitMatchReason.NoFitFound);
            else if (candidates.Length == 1)
                (selected, reason) = (candidates[0], ShipFitMatchReason.OnlyFitForShipType);
            else
                (selected, reason) = (null, ShipFitMatchReason.AmbiguousShipType);
        }

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
            if (!setting.Key.StartsWith(OverrideKeyPrefix, StringComparison.Ordinal) ||
                !int.TryParse(setting.Key[OverrideKeyPrefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out int characterId) ||
                !int.TryParse(setting.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int fittingId))
                continue;

            _manualFits[characterId] = fittingId;
        }
    }

    private static string _OverrideKey(int characterId) => OverrideKeyPrefix + characterId.ToString(CultureInfo.InvariantCulture);
}
