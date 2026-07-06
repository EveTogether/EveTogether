using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Shared.Modules.Dogma;
using EveUtils.Shared.Modules.Fittings.Dtos;

namespace EveUtils.Client.ViewModels.FitBrowser;

/// <summary>
/// Computes a fit's stats for the detail window, backed by the Dogma engine at all-level-5. Returns
/// null when the SDE/engine is unavailable (e.g. the SDE has not been imported yet), so the window can show a notice.
/// </summary>
public interface IFitStatsProvider
{
    /// <summary>Computes the stats with each module at its in-game default state.</summary>
    Task<FitStats?> ComputeAsync(EsiFitting fit, CancellationToken cancellationToken = default);

    /// <summary>Recomputes the stats with explicit module states (and an optional Tactical Destroyer stance), so the
    /// detail window can switch a module offline/online/active/overloaded or change mode and refresh the panels live
    /// . <paramref name="activeDrones"/> are the drones the user has deployed from the bay; when null the
    /// engine auto-deploys the strongest that fit the bandwidth (the initial state), otherwise only those are flown so
    /// drone DPS reflects the selection. <paramref name="boosters"/> are combat boosters the user is simulating (applied
    /// as char-anchored implants; their side-effects stay gated off). <paramref name="skills"/> selects the skill
    /// levels — null/AllLevelFive is the planning baseline, or a character's imported levels.
    /// <paramref name="profile"/> selects the incoming damage mix for weighted EHP; null = Uniform.
    /// <paramref name="weather"/> selects an environment/weather effect beacon applied to the ship; null = none,
    /// so the result is byte-identical to no weather.</summary>
    Task<FitStats?> ComputeAsync(EsiFitting fit, IReadOnlyList<ModuleInput> modules, int? tacticalModeTypeId = null,
        IReadOnlyList<DroneInput>? activeDrones = null, IReadOnlyList<ImplantInput>? boosters = null,
        SkillSource? skills = null, DamageProfile? profile = null, WeatherInput? weather = null,
        IReadOnlyList<FighterInput>? activeFighters = null, CancellationToken cancellationToken = default);
}
