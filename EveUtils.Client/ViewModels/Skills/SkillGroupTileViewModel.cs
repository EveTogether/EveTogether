using CommunityToolkit.Mvvm.ComponentModel;

namespace EveUtils.Client.ViewModels.Skills;

/// <summary>One tile in the CATALOGUE group block (ET-16, mockup v5): a skill group with how many of its skills are
/// injected, and a bar for the share of its levels trained. A group with nothing injected is dimmed, not hidden.</summary>
public sealed partial class SkillGroupTileViewModel(int groupId, string name, int injectedCount, int totalCount, double trainedShare)
    : ObservableObject
{
    public int GroupId { get; } = groupId;
    public string Name { get; } = name;
    public int InjectedCount { get; } = injectedCount;
    public int TotalCount { get; } = totalCount;
    public double TrainedShare { get; } = trainedShare;
    public bool IsEmpty => InjectedCount == 0;
    public string CountText => $"{InjectedCount}/{TotalCount}";

    [ObservableProperty] private bool _isSelected;
}
