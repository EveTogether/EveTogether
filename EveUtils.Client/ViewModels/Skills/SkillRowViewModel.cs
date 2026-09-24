using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Client.Skills;

namespace EveUtils.Client.ViewModels.Skills;

/// <summary>One skill row under a selected group in CATALOGUE (ET-16): level pips, and either the trained mark,
/// the queued target level, or the plain time to the next level — the ticket's own three states.</summary>
public sealed partial class SkillRowViewModel(
    int skillTypeId, string name, int currentLevel, int? trainingLevel, string statusText) : ObservableObject
{
    public int SkillTypeId { get; } = skillTypeId;
    public string Name { get; } = name;
    public int CurrentLevel { get; } = currentLevel;
    public string PipsText { get; } = SkillLevelPips.Text(currentLevel, trainingLevel);
    public string StatusText { get; } = statusText;

    [ObservableProperty] private bool _isSelected;
}
