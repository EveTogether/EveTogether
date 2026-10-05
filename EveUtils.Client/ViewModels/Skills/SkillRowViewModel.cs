using CommunityToolkit.Mvvm.ComponentModel;

namespace EveUtils.Client.ViewModels.Skills;

/// <summary>One skill row in CATALOGUE (ET-16, mockup v5): the pips, the name, and on the right either ✓ (at V), a
/// "QUEUE → IV" chip with the time until the last queued level lands, or the time to the next level ("II in 3h 32m",
/// "not injected · 45m").</summary>
public sealed partial class SkillRowViewModel(
    int skillTypeId, string name, string description, int currentLevel, int queuedLevel, int trainingLevel, bool isInjected,
    string statusText, string queueChipText, string timeText) : ObservableObject
{
    public int SkillTypeId { get; } = skillTypeId;
    public string Name { get; } = name;
    public string Description { get; } = description;
    public int CurrentLevel { get; } = currentLevel;
    public int QueuedLevel { get; } = queuedLevel;
    public int TrainingLevel { get; } = trainingLevel;
    public bool IsInjected { get; } = isInjected;
    public bool IsMastered => CurrentLevel >= 5;
    public string StatusText { get; } = statusText;
    public string QueueChipText { get; } = queueChipText;
    public bool HasQueueChip => QueueChipText.Length > 0;
    public string TimeText { get; } = timeText;

    [ObservableProperty] private bool _isSelected;
}
