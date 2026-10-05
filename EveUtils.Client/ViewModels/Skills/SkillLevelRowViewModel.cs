namespace EveUtils.Client.ViewModels.Skills;

/// <summary>One row (I-V) of a skill's LEVELS table in the SKILLS detail pane (ET-16): total SP to reach it, the time
/// the level takes at today's attributes, and its state (trained, training, queued with its date, or —).</summary>
public sealed record SkillLevelRowViewModel(int Level, string RomanLevel, long TotalSp, bool IsTrained, string TimeText,
    string StatusText, bool IsTraining, bool IsQueued);
