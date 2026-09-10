using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Client.Formatting;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>
/// An evening as a band with a day total, not as a stored thing. "What did I do tonight" is answerable by adding up
/// the rows that are already on screen, so nothing here is saved or synchronised — the moment it were an entity it
/// would need both (ET-131, design question 1).
/// </summary>
public sealed partial class RunsDayViewModel : ObservableObject
{
    public RunsDayViewModel(DateTime day, IReadOnlyList<ActivityOverviewRowViewModel> rows)
    {
        Day = day;
        DayText = day.ToString("dddd d MMMM", CultureInfo.InvariantCulture).ToUpperInvariant();
        Show(rows);
    }

    /// <summary>The date this band groups by — and what a refresh recognises it by, so the band itself, open or
    /// folded, stays on screen while its rows and total move (ET-189, ET-222).</summary>
    public DateTime Day { get; }

    public string DayText { get; }

    [ObservableProperty] private string _summaryText = string.Empty;

    public ObservableCollection<ActivityOverviewRowViewModel> Rows { get; } = [];

    /// <summary>Folded by default (ET-199) — <see cref="RunsOverviewViewModel"/> opens only the most recent day
    /// once it knows which one that is, since a fresh <see cref="RunsDayViewModel"/> has no way to tell where it
    /// falls among the others being built alongside it.</summary>
    [ObservableProperty] private bool _isExpanded;

    /// <summary>This evening's rows as they now stand, and the day total over them.</summary>
    public void Show(IReadOnlyList<ActivityOverviewRowViewModel> rows)
    {
        Rows.ReconcileTo(rows);

        var flown = TimeSpan.FromSeconds(rows.Sum(row => row.Duration.TotalSeconds));
        string activities = $"{rows.Count} {(rows.Count == 1 ? "activity" : "activities")}";
        string flownText = $"{(int)flown.TotalHours}:{flown.Minutes:00}:{flown.Seconds:00} flown";

        // The day says nothing rather than "0 ISK" when not one of its activities has a figure, for the same reason
        // the row does: a zero here would read as an evening that was valued and came to nothing.
        decimal[] known = [.. rows.Where(row => row.NetIsk.HasValue).Select(row => row.NetIsk.GetValueOrDefault())];
        string netText = known.Length == 0
            ? "nothing recorded to value"
            : (known.Sum() < 0 ? string.Empty : "+") + IskFormat.Compact(known.Sum()) + " ISK net";

        SummaryText = $"{activities} · {flownText} · {netText}";
    }
}
