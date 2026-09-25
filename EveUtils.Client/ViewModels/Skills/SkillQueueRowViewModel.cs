using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Client.Skills;
using RomanLevel = EveUtils.Client.Skills.RomanLevel;

namespace EveUtils.Client.ViewModels.Skills;

/// <summary>One row of the TRAINING QUEUE table (ET-16): position, the skill and its target level, this level's
/// own duration, when it ends (or "—" while paused) and the cumulative time from now.</summary>
public sealed partial class SkillQueueRowViewModel(
    int position, int skillTypeId, string skillName, int currentLevel, int finishedLevel, string groupName, bool isTraining,
    string thisLevelText, string endsText, string fromNowText) : ObservableObject
{
    public int Position { get; } = position;
    public int SkillTypeId { get; } = skillTypeId;
    public string SkillText { get; } = $"{skillName} {RomanLevel.Text(finishedLevel)}";
    public string PipsText { get; } = SkillLevelPips.Text(currentLevel, isTraining ? finishedLevel : null);
    public string GroupName { get; } = groupName;
    public bool IsTraining { get; } = isTraining;
    public string ThisLevelText { get; } = thisLevelText;
    public string EndsText { get; } = endsText;
    public string FromNowText { get; } = fromNowText;

    [ObservableProperty] private bool _isSelected;
}
