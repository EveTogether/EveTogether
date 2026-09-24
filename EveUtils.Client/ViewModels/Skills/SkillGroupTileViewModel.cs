using CommunityToolkit.Mvvm.ComponentModel;

namespace EveUtils.Client.ViewModels.Skills;

/// <summary>One tile in the CATALOGUE group grid (ET-16): a skill group with how many of its skills are injected.</summary>
public sealed partial class SkillGroupTileViewModel(int groupId, string name, int injectedCount, int totalCount) : ObservableObject
{
    public int GroupId { get; } = groupId;
    public string Name { get; } = name;
    public int InjectedCount { get; } = injectedCount;
    public int TotalCount { get; } = totalCount;
    public string CountText => $"{InjectedCount}/{TotalCount}";

    [ObservableProperty] private bool _isSelected;
}
