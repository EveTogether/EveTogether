using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Shared.Modules.Runs.Isk;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>
/// An evening as a day header with a day total, not as a stored thing. "What did I do tonight" is answerable by adding
/// up the rows that are already on screen, so nothing here is saved or synchronised — the moment it were an entity it
/// would need both (ET-131, design question 1).
///
/// Its fold state lives here, on the day, and not on a list container (ET-290): the list is virtualised, so the
/// container that drew this header a moment ago may be drawing another row now.
/// </summary>
public sealed partial class RunsDayViewModel : ObservableObject
{
    private readonly Func<RunsDayViewModel, Task> _publishLocal;

    public RunsDayViewModel(DateTime day, IReadOnlyList<ActivityOverviewRowViewModel> rows, bool canPublish,
        bool isPublishing, Func<RunsDayViewModel, Task> publishLocal)
    {
        _publishLocal = publishLocal;
        Day = day;
        DayText = day.ToString("dddd d MMMM", CultureInfo.InvariantCulture).ToUpperInvariant();
        WeekdayText = day.ToString("dddd", CultureInfo.InvariantCulture).ToUpperInvariant();
        DateText = day.ToString("d MMMM", CultureInfo.InvariantCulture).ToUpperInvariant();
        Show(rows, canPublish, isPublishing);
    }

    /// <summary>The date this day groups by — and what a refresh recognises it by, so the day itself, open or folded,
    /// stays on screen while its rows and total move (ET-189, ET-222).</summary>
    public DateTime Day { get; }

    public string DayText { get; }

    public string WeekdayText { get; }

    public string DateText { get; }

    [ObservableProperty] private string _summaryText = string.Empty;

    [ObservableProperty] private string _countText = string.Empty;

    [ObservableProperty] private string _flownText = string.Empty;

    [ObservableProperty] private string _netText = string.Empty;

    [ObservableProperty] private string _countAndFlownText = string.Empty;

    [ObservableProperty] private IskBreakdown _isk = IskBreakdown.None;

    public ObservableCollection<ActivityOverviewRowViewModel> Rows { get; } = [];

    /// <summary>Folded by default (ET-199) — <see cref="RunsTabViewModel"/> opens only the most recent day once it
    /// knows which one that is, since a fresh <see cref="RunsDayViewModel"/> has no way to tell where it falls among
    /// the others being built alongside it.</summary>
    [ObservableProperty] private bool _isExpanded;

    /// <summary>The tab's list follows a fold at once: the day's rows leave it or come back.</summary>
    public event Action<RunsDayViewModel>? ExpandedChanged;

    partial void OnIsExpandedChanged(bool value) => ExpandedChanged?.Invoke(this);

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;

    /// <summary>Never queued for, or on, any server — this day's own count of what PUBLISH n LOCAL would send.
    /// Always 0 on a server tab: every row it holds is filtered to ones already published to that server (RO-6).</summary>
    [ObservableProperty] private int _localCount;

    /// <summary>Whether the button shows at all: a server coupled and something local to send. Independent of
    /// <see cref="CanPublishLocal"/>, which also asks the screen is not already publishing — hiding the button while
    /// busy would take away the "PUBLISHING…" it is meant to say.</summary>
    [ObservableProperty] private bool _showPublishLocal;

    [ObservableProperty] private bool _canPublishLocal;

    [ObservableProperty] private string _publishLocalButtonText = string.Empty;

    [RelayCommand]
    private async Task PublishLocalAsync() => await _publishLocal(this);

    /// <summary>This evening's rows as they now stand, and the day total over them. Still said while the day is
    /// folded (ET-199 AC-4): a folded evening keeps its piece in the header.</summary>
    public void Show(IReadOnlyList<ActivityOverviewRowViewModel> rows, bool canPublish, bool isPublishing)
    {
        Rows.ReconcileTo(rows);

        CountText = RunsActivitySummaryText.ActivitiesCount(rows.Count);
        FlownText = RunsActivitySummaryText.FlownFor(rows);
        NetText = RunsActivitySummaryText.NetFor(rows);
        CountAndFlownText = $"{CountText} · {FlownText}";
        SummaryText = $"{CountAndFlownText} · {NetText}";
        Isk = RunsActivitySummaryText.SourcesFor(rows);

        LocalCount = rows.Count(row => row.IsLocal);
        ShowPublishLocal = canPublish && LocalCount > 0;
        CanPublishLocal = ShowPublishLocal && !isPublishing;
        PublishLocalButtonText = isPublishing ? "PUBLISHING…" : $"{LocalCount} local ↑";
    }
}
