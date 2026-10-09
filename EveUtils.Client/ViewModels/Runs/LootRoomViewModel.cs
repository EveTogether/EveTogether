using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>One room of a LOOT block (ET-240): the items copied while it was going, with their own subtotal. Folds
/// away on its header; the block's total under it stays the whole run's.</summary>
public sealed partial class LootRoomViewModel(int number, string windowText, string subtotalText,
    IReadOnlyList<ActivityLootLineViewModel> rows) : ObservableObject
{
    public int Number { get; } = number;

    public string Title { get; } = $"ROOM {number}";

    public string WindowText { get; } = windowText;

    public string SubtotalText { get; } = subtotalText;

    public IReadOnlyList<ActivityLootLineViewModel> Rows { get; } = rows;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Chevron))]
    private bool _isExpanded = true;

    public string Chevron => IsExpanded ? "▾" : "▸";

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;
}
