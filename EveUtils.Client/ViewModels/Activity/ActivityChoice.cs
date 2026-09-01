using CommunityToolkit.Mvvm.ComponentModel;

namespace EveUtils.Client.ViewModels.Activity;

/// <summary>
/// One button in the weather or tier picker. It carries its own index so the row can be an <c>ItemsControl</c> over
/// a flat list and the command still knows which of the five (or seven) was pressed.
/// </summary>
public sealed partial class ActivityChoice : ObservableObject
{
    public required int Index { get; init; }

    public required string Label { get; init; }

    /// <summary>What this choice does to the ship, for the hover. The picker is two clicks precisely because the
    /// buttons carry no explanation on their face.</summary>
    public string? Tooltip { get; init; }

    [ObservableProperty] private bool _isSelected;
}
