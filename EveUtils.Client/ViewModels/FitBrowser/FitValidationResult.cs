using System.Collections.Generic;

namespace EveUtils.Client.ViewModels.FitBrowser;

/// <summary>
/// Whether a fit is flyable for the selected character: the skills it lacks and the fitting resources it
/// overloads. Pure compute — the UI (a later pass) turns this into the "Skills Required" panel, the fitting-alert
/// banner and the header badges. Empty lists = a valid fit.
/// </summary>
public sealed record FitValidationResult(
    IReadOnlyList<SkillGap> SkillGaps,
    IReadOnlyList<ResourceOverload> Overloads)
{
    public bool IsValid => SkillGaps.Count == 0 && Overloads.Count == 0;

    public static FitValidationResult Empty { get; } = new([], []);
}
