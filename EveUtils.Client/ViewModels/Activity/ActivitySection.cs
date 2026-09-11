using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Client.ViewModels.Runs.Sections;

namespace EveUtils.Client.ViewModels.Activity;

/// <summary>
/// One collapsible section of the run window or the activity detail screen — the shape every section module shares
/// (ET-236). <see cref="HeaderSummary"/> is the reason a section can be folded shut at all: a closed section still has
/// to answer for itself, so a section that is empty because it waits on another ticket says exactly that instead of
/// showing a blank line the reader has to interpret.
///
/// Its body is the module's own view, found by the app's <c>ViewLocator</c> from the concrete section's type name.
/// </summary>
public abstract partial class ActivitySection(RunSectionId id, string title) : ViewModelBase
{
    public RunSectionId Id { get; } = id;

    public string Title { get; } = title;

    [ObservableProperty] private string _headerSummary = string.Empty;

    [ObservableProperty] private bool _isExpanded;

    /// <summary>Whether the section stands on screen at all, for a section that decides that for itself — FLEET
    /// before any fleet has reported in. Which sections a run has is its type's, not this.</summary>
    public virtual bool IsShown => true;
}
