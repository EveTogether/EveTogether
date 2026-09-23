using System;
using System.Threading.Tasks;
using EveUtils.Client.ViewModels.Runs;

namespace EveUtils.Client.ViewModels.Home;

/// <summary>Where the home's links lead: the shell's own entry points, so a click on home opens exactly what the rail
/// or the character column would.</summary>
/// <param name="OpenRuns">Opens the runs overview, then hands the one on screen to the continuation (a range to pick).</param>
/// <param name="LaunchModule">A rail module by its id ("fits", "fleet", "inbox", …).</param>
/// <param name="OpenFleets">Opens the fleets screen, then hands the one on screen to the continuation (START, NEW FLEET).</param>
/// <param name="OpenCharacterSettings">The character settings dialog.</param>
/// <param name="OpenMetrics">A character's metrics window.</param>
/// <param name="OpenDpsOverlay">A character's live DPS overlay.</param>
/// <param name="AllowScope">Re-authorise a character with one more scope ticked in the scope picker (D-21).</param>
/// <param name="ImportFittings">IMPORT FROM EVE: the fit import with its own character picker.</param>
public sealed record HomeNavigation(
    Func<Func<RunsOverviewViewModel, Task>?, Task> OpenRuns,
    Action<string> LaunchModule,
    Func<Func<FleetsViewModel, Task>?, Task> OpenFleets,
    Func<CharacterViewModel, Task> OpenCharacterSettings,
    Action<CharacterViewModel> OpenMetrics,
    Action<CharacterViewModel> OpenDpsOverlay,
    Func<int, string, Task> AllowScope,
    Func<Task> ImportFittings)
{
    public static readonly HomeNavigation None = new(
        _ => Task.CompletedTask, _ => { }, _ => Task.CompletedTask, _ => Task.CompletedTask, _ => { }, _ => { },
        (_, _) => Task.CompletedTask, () => Task.CompletedTask);
}
