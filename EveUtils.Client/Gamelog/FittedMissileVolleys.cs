using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using EveUtils.Client.Esi;
using EveUtils.Client.ViewModels.FitBrowser;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Dogma;
using EveUtils.Shared.Modules.Fittings.Dtos;
using EveUtils.Shared.Modules.Fittings.Repositories;
using EveUtils.Shared.Modules.Gamelog.Aggregation;
using EveUtils.Shared.Modules.Implants.Repositories;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Skills.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EveUtils.Client.Gamelog;

/// <summary>
/// A character's full missile volley from the fit they are flying (ET-282): the fit ET-101 detected for their current
/// ship, every launcher in it that takes the missile the log names loaded with it, their own skills and implants, through
/// the dogma engine. The launchers are taken to fire as one group, which is how the log writes a volley.
///
/// Worked out off the reading thread and kept for a while, so a skill trained or a fit edited reaches it within minutes;
/// until the first result is in, and whenever no fit is known, there is none and the gauge learns instead.
/// </summary>
public sealed class FittedMissileVolleys(IServiceProvider services) : ISingletonService
{
    // Kept this long before it is worked out again from whatever the fit, skills and implants are then.
    private static readonly TimeSpan Freshness = TimeSpan.FromMinutes(10);

    // A charge names the launcher groups that take it.
    private static readonly int[] LauncherGroupAttributes = [137, 602, 603, 2076, 2077, 2078];

    private readonly ConcurrentDictionary<(int CharacterId, int FittingId, int MissileTypeId), Entry> _volleys = new();

    /// <summary>The volley, or null when no fit is known, it has no launcher for this missile, or it is still being
    /// worked out.</summary>
    public MissileVolley? Find(int characterId, string missile)
    {
        if (services.GetService<IShipFitDetectionService>()?.GetReading(characterId).SelectedFit is not { } fit
            || services.GetService<ISdeAccessor>() is not { IsAvailable: true } sde
            || !sde.TryGetTypeId(missile, out var missileTypeId))
            return null;

        var key = (characterId, fit.Id, missileTypeId);
        var entry = _volleys.GetOrAdd(key, _ => _Start(key, sde, previous: null));
        if (DateTime.UtcNow - entry.StartedAt > Freshness && entry.Volley.IsCompleted)
            _volleys[key] = _Start(key, sde, entry.Result);
        return entry.Result;
    }

    // Off the caller's thread: it is asked from under the application tracker's lock, on the screen's render tick.
    private Entry _Start((int CharacterId, int FittingId, int MissileTypeId) key, ISdeAccessor sde, MissileVolley? previous) =>
        new(Task.Run(() => _CalculateAsync(key, sde)), DateTime.UtcNow) { Previous = previous };

    private async Task<MissileVolley?> _CalculateAsync((int CharacterId, int FittingId, int MissileTypeId) key, ISdeAccessor sde)
    {
        try
        {
            var data = services.GetRequiredService<IDogmaDataAccessor>();
            var fitting = await services.GetRequiredService<IFittingRepository>().FindByIdAsync(key.FittingId);
            if (fitting is null || JsonSerializer.Deserialize<EsiFitting>(fitting.RawJson) is not { } esi)
                return null;
            var levels = await services.GetRequiredService<ICharacterSkillRepository>().GetLevelsAsync(key.CharacterId);
            if (levels.Count == 0)
                return null;
            var implants = await services.GetRequiredService<ICharacterImplantRepository>().GetTypeIdsAsync(key.CharacterId);

            var launcherGroups = sde.GetDogmaAttributes(key.MissileTypeId)
                .Where(attribute => LauncherGroupAttributes.Contains(attribute.AttributeId))
                .Select(attribute => (int)attribute.Value)
                .ToHashSet();
            var modules = FitInputMapper.BuildModules(esi, sde, data)
                .Select(module => launcherGroups.Contains(data.GetGroupId(module.TypeId) ?? 0)
                    ? module with { State = ModuleState.Active, ChargeTypeId = key.MissileTypeId }
                    : module)
                .ToList();

            var result = await services.GetRequiredService<IDogmaCalculator>().CalculateAsync(new FitInput(esi.ShipTypeId,
                modules, SkillSource.From(levels), Implants: implants.Select(typeId => new ImplantInput(typeId)).ToList()));
            var launchers = result.Contributions
                .Where(contribution => contribution.Kind is ModuleContributionKind.Missile && contribution.ChargeTypeId == key.MissileTypeId)
                .ToList();
            return launchers.Count == 0
                ? null
                : new MissileVolley(
                    launchers.Sum(launcher => launcher.DamageEm + launcher.DamageThermal + launcher.DamageKinetic + launcher.DamageExplosive),
                    launchers[0].ExplosionRadius, launchers[0].ExplosionVelocity);
        }
        catch (Exception ex)
        {
            // A fit that no longer parses or an SDE swapped mid-read: no fit volley, and the gauge learns instead.
            services.GetService<ILogger<FittedMissileVolleys>>()?.LogWarning(ex,
                "Could not work out the missile volley of fitting {FittingId} for {CharacterId}", key.FittingId, key.CharacterId);
            return null;
        }
    }

    // While a fresh calculation runs, the previous result keeps standing.
    private sealed record Entry(Task<MissileVolley?> Volley, DateTime StartedAt)
    {
        public MissileVolley? Previous { get; init; }

        public MissileVolley? Result => Volley.IsCompletedSuccessfully ? Volley.Result : Previous;
    }
}
