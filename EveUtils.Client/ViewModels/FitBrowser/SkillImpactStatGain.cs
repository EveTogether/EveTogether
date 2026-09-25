using System.Globalization;
using EveUtils.Shared.Modules.Skills;

namespace EveUtils.Client.ViewModels.FitBrowser;

/// <summary>One selected stat's value for a skill-impact row, at the skill's next level and at level V — display
/// values, not deltas; <see cref="DisplayText"/> is what the row actually shows.</summary>
public sealed record SkillImpactStatGain(SkillImpactStat Stat, string Label, double AtNextLevel, double AtFive)
{
    public string DisplayText =>
        $"{Label} {AtFive.ToString("0.##", CultureInfo.InvariantCulture)} (+1: {AtNextLevel.ToString("0.##", CultureInfo.InvariantCulture)})";
}
