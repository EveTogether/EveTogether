using EveUtils.Shared.Modules.Skills;

namespace EveUtils.Client.ViewModels.FitBrowser;

/// <summary>One missing-skill row in the fit-detail "Skills Required" panel: the skill name, the level the fit
/// needs (Roman) and what the character has trained, plus — when the character's attributes are known — the SP still to
/// train and the Omega time it takes at their effective rate (base + attribute implants). Read-only, no buy/train.</summary>
public sealed class SkillGapViewModel
{
    public string Name { get; }
    public string RequiredRoman { get; }
    public string Detail { get; }

    /// <summary>The SP-to-train + Omega time ("210.7k SP · 4d 4h 21m"), or null when no character attributes are
    /// available (e.g. the all-V baseline, or attributes not yet imported).</summary>
    public string? Estimate { get; }

    public bool HasEstimate => Estimate is not null;

    public SkillGapViewModel(string name, int requiredLevel, int currentLevel, SkillTrainingEstimate? estimate = null)
    {
        Name = name;
        RequiredRoman = Roman(requiredLevel);
        Detail = currentLevel > 0
            ? $"trained {Roman(currentLevel)} — needs {Roman(requiredLevel)}"
            : $"needs {Roman(requiredLevel)}";
        Estimate = estimate is null ? null : SkillEstimateFormat.SpAndTime(estimate.SkillPointsRequired, estimate.TrainingTime);
    }

    private static string Roman(int level) => level switch
    {
        1 => "I",
        2 => "II",
        3 => "III",
        4 => "IV",
        5 => "V",
        _ => level.ToString()
    };
}
