using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>
/// An evening as a band with a day total, not as a stored thing. "What did I do tonight" is answerable by adding up
/// the rows that are already on screen, so nothing here is saved or synchronised — the moment it were an entity it
/// would need both (ET-131, design question 1).
/// </summary>
public sealed class RunsDayViewModel
{
    public RunsDayViewModel(DateTime day, IReadOnlyList<ActivityOverviewRowViewModel> rows)
    {
        Rows = [.. rows];
        DayText = day.ToString("dddd d MMMM", CultureInfo.InvariantCulture).ToUpperInvariant();

        var flown = TimeSpan.FromSeconds(rows.Sum(row => row.Duration.TotalSeconds));
        string activities = $"{rows.Count} {(rows.Count == 1 ? "activity" : "activities")}";
        string flownText = $"{(int)flown.TotalHours}:{flown.Minutes:00}:{flown.Seconds:00} flown";

        // The day says nothing rather than "0 ISK" when not one of its activities has a loot capture, for the same
        // reason the row does: a zero here would read as an evening that was valued and came to nothing.
        decimal[] known = [.. rows.Where(row => row.NetIsk.HasValue).Select(row => row.NetIsk!.Value)];
        string netText = known.Length == 0
            ? "no loot captured"
            : (known.Sum() < 0 ? string.Empty : "+") + ActivityRewardChipViewModel.Compact(known.Sum()) + " ISK net";

        SummaryText = $"{activities} · {flownText} · {netText}";
    }

    public string DayText { get; }
    public string SummaryText { get; }
    public ObservableCollection<ActivityOverviewRowViewModel> Rows { get; }
}
