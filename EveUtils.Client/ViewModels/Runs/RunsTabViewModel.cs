using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>
/// One source on the runs screen: the local history, or one coupled server. Mirrors the fit browser's tab strip
/// (Local first, then a tab per coupled server, none at all when no server is coupled).
///
/// Unlike a fit-browser server tab this one does not load lazily and has no address of its own to fetch from: every
/// row here is a local activity, filtered on the server its runs were published to. The days are handed in by
/// <see cref="RunsOverviewViewModel"/> from the one overview read, so a server tab cannot disagree with Local about
/// an activity they both show.
/// </summary>
public sealed partial class RunsTabViewModel(string header, string? serverAddress) : ObservableObject
{
    public string Header { get; } = header;

    public string? ServerAddress { get; } = serverAddress;

    public bool IsLocal => ServerAddress is null;

    public ObservableCollection<RunsDayViewModel> Days { get; } = [];

    /// <summary>Why this tab is empty, when it is. A server tab and the local tab are empty for different reasons and
    /// say so.</summary>
    [ObservableProperty] private string? _statusMessage;

    /// <summary>The viewed month's own total (ET-233): the same formula every day band below sums its own rows
    /// with, over every row this tab holds regardless of which days are folded — a month is always complete, and so
    /// is its total. Split in two (ET-266) so the month bar can give the ISK figure more weight than the activity
    /// count, unlike the day band below which keeps both in one string.</summary>
    [ObservableProperty] private string _monthActivitiesText = string.Empty;

    [ObservableProperty] private string _monthNetText = string.Empty;

    public void UpdateMonthSummary()
    {
        List<ActivityOverviewRowViewModel> rows = [.. Days.SelectMany(day => day.Rows)];
        MonthActivitiesText = $"{RunsActivitySummaryText.ActivitiesCount(rows.Count)} this month";
        MonthNetText = RunsActivitySummaryText.NetFor(rows);
    }
}
