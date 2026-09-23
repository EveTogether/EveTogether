using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Formatting;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Shared.Modules.Runs.Dtos;

namespace EveUtils.Client.ViewModels.Home;

/// <summary>A day header over the latest runs: that whole day's figures, not only the rows shown under it.</summary>
public sealed record HomeRunDay(DateOnly Day, string DayText, string SummaryText, string IskText);

/// <summary>A latest run as the runs overview draws it; the last one only where there is room for it.</summary>
public sealed record HomeRunLine(ActivityOverviewRowViewModel Row, bool IsWideOnly);

/// <summary>One of the best drops: the item, how many, and what they are worth at the stored average price.</summary>
public sealed record HomeDropLine(string Name, string QuantityText, string ValueText, string Tooltip);

/// <summary>
/// LATEST RUNS and BEST DROPS · 7 DAYS (ET-324): the newest runs as runs-overview rows under their day, what only lives
/// on this PC with PUBLISH, and the most valuable loot of the week. Nothing here reads — the home's one runs read hands
/// the rows over, built off the UI thread.
/// </summary>
public sealed partial class HomeLatestRunsViewModel(
    Func<IReadOnlyList<Guid>, Task> publish,
    Action openOverview) : ObservableObject
{
    /// <summary>Wide shows six, narrow five (the design): the sixth carries <see cref="HomeRunLine.IsWideOnly"/>.</summary>
    public const int Shown = 6;

    public const int ShownDrops = 4;

    private IReadOnlyList<Guid> _localIds = [];

    /// <summary>Day headers (<see cref="HomeRunDay"/>) and rows (<see cref="HomeRunLine"/>), newest first.</summary>
    public ObservableCollection<object> Items { get; } = [];

    public ObservableCollection<HomeDropLine> Drops { get; } = [];

    [ObservableProperty] private bool _hasRuns;
    [ObservableProperty] private bool _hasDrops;
    [ObservableProperty] private bool _showPublish;
    [ObservableProperty] private bool _isPublishing;
    [ObservableProperty] private string _localText = string.Empty;
    [ObservableProperty] private string _publishText = string.Empty;

    /// <param name="rows">The newest rows, already built (and reused where unchanged) off the UI thread.</param>
    /// <param name="days">Every activity of the days those rows fall on, for the day headers' totals.</param>
    /// <param name="serverName">The one coupled server's name, or null with none or several.</param>
    internal void Show(IReadOnlyList<ActivityOverviewRowViewModel> rows, ILookup<DateOnly, RunsActivityFacts> days,
        DateOnly today, IReadOnlyList<Guid> localIds, bool canPublish, string? serverName)
    {
        List<object> items = [];
        Dictionary<DateOnly, HomeRunDay> shownDays = Items.OfType<HomeRunDay>().ToDictionary(header => header.Day);
        Dictionary<ActivityOverviewRowViewModel, HomeRunLine> shownLines = Items.OfType<HomeRunLine>().ToDictionary(line => line.Row);

        DateOnly? previous = null;
        for (int index = 0; index < rows.Count; index++)
        {
            ActivityOverviewRowViewModel row = rows[index];
            DateOnly day = DateOnly.FromDateTime(row.StartedAtLocal);
            if (day != previous)
            {
                HomeRunDay header = _Day(day, today, [.. days[day]]);
                items.Add(shownDays.TryGetValue(day, out HomeRunDay? shown) && shown == header ? shown : header);
                previous = day;
            }

            bool isWideOnly = index == Shown - 1;
            items.Add(shownLines.TryGetValue(row, out HomeRunLine? line) && line.IsWideOnly == isWideOnly
                ? line
                : new HomeRunLine(row, isWideOnly));
        }

        Items.ReconcileTo(items);
        HasRuns = rows.Count > 0;

        _localIds = localIds;
        ShowPublish = canPublish && localIds.Count > 0;
        LocalText = $"{localIds.Count} saved run{(localIds.Count == 1 ? " is" : "s are")} only on this PC";
        PublishText = serverName is null ? $"PUBLISH {localIds.Count}…" : $"PUBLISH {localIds.Count} TO {serverName}";
    }

    internal void ShowDrops(IReadOnlyList<BestDropDto> drops)
    {
        HomeDropLine[] lines = [.. drops.Select(drop => new HomeDropLine(drop.Name,
            "×" + drop.Quantity.ToString("N0", CultureInfo.InvariantCulture), IskFormat.Compact(drop.Value),
            $"{drop.Quantity.ToString("N0", CultureInfo.InvariantCulture)} × ESI average price = {IskFormat.Whole(drop.Value)}"))];
        if (!lines.SequenceEqual(Drops))
            Drops.ReconcileTo([.. lines.Select(line => Drops.FirstOrDefault(shown => shown == line) ?? line)]);
        HasDrops = lines.Length > 0;
    }

    [RelayCommand]
    private void OpenOverview() => openOverview();

    [RelayCommand]
    private async Task PublishAsync()
    {
        if (IsPublishing || _localIds.Count == 0)
            return;

        IsPublishing = true;
        try
        {
            await publish(_localIds);
        }
        finally
        {
            IsPublishing = false;
        }
    }

    private static HomeRunDay _Day(DateOnly day, DateOnly today, IReadOnlyList<RunsActivityFacts> activities)
    {
        string date = day.ToString("ddd d MMM", CultureInfo.InvariantCulture).ToUpperInvariant();
        TimeSpan flown = TimeSpan.FromSeconds(activities.Sum(activity => activity.Duration.TotalSeconds));
        return new HomeRunDay(
            day,
            day == today ? "TODAY · " + date : date,
            $"{activities.Count} run{(activities.Count == 1 ? "" : "s")} · {HomeEarningsTileViewModel.FlownText(flown)}",
            RunsActivitySummaryText.Net(activities) is { } net ? IskFormat.Compact(net) : "—");
    }
}
