using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>A line in the runs list that is neither a day, an activity nor a pilot — why an unfolded activity shows
/// no runs, when its read failed.</summary>
public sealed record RunsListNote(string Text);

/// <summary>
/// One source on the runs screen: the local history, or one coupled server. Mirrors the fit browser's tab strip
/// (Local first, then a tab per coupled server, none at all when no server is coupled).
///
/// Unlike a fit-browser server tab this one does not load lazily and has no address of its own to fetch from: every
/// row here is a local activity, filtered on the server its runs were published to. The rows are handed in by
/// <see cref="RunsOverviewViewModel"/> from the one overview read, so a server tab cannot disagree with Local about
/// an activity they both show.
/// </summary>
public sealed partial class RunsTabViewModel(string header, string? serverAddress) : ObservableObject
{
    private bool _isShowing;

    public string Header { get; } = header;

    public string? ServerAddress { get; } = serverAddress;

    public bool IsLocal => ServerAddress is null;

    public ObservableCollection<RunsDayViewModel> Days { get; } = [];

    /// <summary>
    /// The list as it is drawn (ET-290): each day's header, then — while the day is unfolded — its activities, each
    /// followed by its pilots' runs while that activity is unfolded. One flat sequence, so the list can virtualise it;
    /// folding takes items out of it rather than hiding them. Reconciled by identity, never cleared and refilled, so a
    /// header or row that stays keeps its place and its container.
    /// </summary>
    public ObservableCollection<object> Items { get; } = [];

    /// <summary>Why this tab is empty, when it is. A server tab and the local tab are empty for different reasons and
    /// say so.</summary>
    [ObservableProperty] private string? _statusMessage;

    /// <summary>Whether an activity belongs under this tab: every one on Local, the ones published to it on a server
    /// tab — the rule <see cref="RunsOverviewViewModel"/> hands rows over by, applied to what the strip counts.</summary>
    public bool Holds(RunsActivityFacts activity) =>
        ServerAddress is not { } address || activity.ServerAddresses.Contains(address);

    /// <summary>
    /// This tab's activities as they now stand, grouped under their local day. A day already on screen is the same day
    /// afterwards, open or folded as the reader left it (ET-189); a row whose figures did not move is the same row
    /// (ET-222), which keeps an opened row open and the scroll offset where it was while payouts land.
    /// </summary>
    public void Show(IReadOnlyList<ActivityOverviewRowViewModel> rows)
    {
        Dictionary<DateTime, RunsDayViewModel> shownDays = Days.ToDictionary(day => day.Day);
        List<RunsDayViewModel> days = [];
        _isShowing = true;
        try
        {
            foreach (IGrouping<DateTime, ActivityOverviewRowViewModel> day in rows.GroupBy(row => row.StartedAtLocal.Date))
            {
                if (shownDays.TryGetValue(day.Key, out RunsDayViewModel? shown))
                {
                    shown.Show([.. day]);
                    days.Add(shown);
                    continue;
                }

                var fresh = new RunsDayViewModel(day.Key, [.. day]);
                fresh.ExpandedChanged += _OnDayExpandedChanged;
                days.Add(fresh);
            }

            Days.ReconcileTo(days);

            // Opens on the most recent day only (ET-199), so he never has to scroll through weeks of history to reach
            // today; every older evening still says its piece folded, in its own header. A day already on screen keeps
            // whatever the reader set for it — this default only ever reaches a day this tab is showing for the first
            // time, the evening's first save included. Browsing to another month opens that month's own most recent
            // day, not "today".
            if (days.MaxBy(day => day.Day) is { } latest && !shownDays.ContainsKey(latest.Day))
                latest.IsExpanded = true;
        }
        finally
        {
            _isShowing = false;
        }

        RebuildItems();
    }

    /// <summary>Raised once the flat sequence is whole again, never per change inside a rebuild (ET-291): the screen's
    /// own selection is settled against it, and a <c>ListBox</c> half way through a reconcile still moves its
    /// <c>SelectedItem</c> around on its own.</summary>
    public event Action<RunsTabViewModel>? ItemsRebuilt;

    /// <summary>Brings <see cref="Items"/> in line with what is folded and unfolded right now.</summary>
    public void RebuildItems()
    {
        List<object> items = [];
        foreach (RunsDayViewModel day in Days)
        {
            items.Add(day);
            if (!day.IsExpanded)
                continue;

            foreach (ActivityOverviewRowViewModel row in day.Rows)
            {
                items.Add(row);
                if (!row.IsExpanded)
                    continue;

                items.AddRange(row.SubRuns);
                if (row.SubRunsNote is { } note)
                    items.Add(note);
            }
        }

        Items.ReconcileTo(items);
        ItemsRebuilt?.Invoke(this);
    }

    /// <summary>Unfolds one day and leaves every other as the reader set it — for the activity strip (ET-292), which
    /// picks a day to show. Null when this tab holds nothing on that day.</summary>
    public RunsDayViewModel? ExpandDay(DateTime dayLocal) => ExpandDays([dayLocal]).FirstOrDefault();

    /// <summary>Unfolds the days this tab holds among these, in the order the list draws them (newest first), in one
    /// rebuild of the list — a picked week is up to seven of them. Every other day keeps its fold.</summary>
    public IReadOnlyList<RunsDayViewModel> ExpandDays(IEnumerable<DateTime> daysLocal)
    {
        HashSet<DateTime> wanted = [.. daysLocal.Select(day => day.Date)];
        List<RunsDayViewModel> expanded = [.. Days.Where(day => wanted.Contains(day.Day))];
        bool anyFolded = expanded.Any(day => !day.IsExpanded);
        _isShowing = true;
        try
        {
            foreach (RunsDayViewModel day in expanded)
                day.IsExpanded = true;
        }
        finally
        {
            _isShowing = false;
        }

        if (anyFolded)
            RebuildItems();
        return expanded;
    }

    private void _OnDayExpandedChanged(RunsDayViewModel day)
    {
        if (!_isShowing)
            RebuildItems();
    }
}
