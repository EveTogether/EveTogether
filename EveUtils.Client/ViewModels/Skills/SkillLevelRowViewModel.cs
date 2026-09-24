namespace EveUtils.Client.ViewModels.Skills;

/// <summary>One row (I-V) of a skill's level breakdown in the SKILLS detail pane (ET-16).</summary>
public sealed record SkillLevelRowViewModel(int Level, string RomanLevel, long TotalSp, bool IsTrained, string StatusText);
