using CommunityToolkit.Mvvm.ComponentModel;

namespace EveUtils.Client.ViewModels.Activity;

/// <summary>
/// One collapsible section of the activity window. <see cref="HeaderSummary"/> is the reason the window can be
/// folded shut at all: a closed section still has to answer for itself, so a section that is empty because it waits
/// on another ticket says exactly that instead of showing a blank line the reader has to interpret.
/// </summary>
public sealed partial class ActivitySection : ObservableObject
{
    public required string Title { get; init; }

    [ObservableProperty] private string _headerSummary = string.Empty;

    [ObservableProperty] private bool _isExpanded;
}
