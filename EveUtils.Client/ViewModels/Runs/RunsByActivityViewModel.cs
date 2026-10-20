using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Runs;
using EveUtils.Shared.Modules.Runs.Earnings;
using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>
/// The runs overview's BY ACTIVITY list (ET-517): one row per activity key instead of one per flown activity, with what
/// each typically earns and the detail of the selected one. It reads its own figures, off the UI thread and once per
/// refresh, and keeps its own rows in its own items source — the virtualised list of RUNS is not touched.
///
/// <para>Whatever the screen already filters by passes through: the TYPES tiles, and the search ET-518 adds (<see cref="SearchFilter"/>,
/// matched on each row's unmarked <see cref="ActivityStatsLabel.SearchText"/>). The CHARACTERS tiles do not: the figures
/// pool the pilot's own characters, which is what makes a site's typical run.</para>
///
/// <para>Selection survives a refresh by key. A row that is not in the list any more (a filter, another month) keeps
/// its detail on screen until the reader picks another.</para>
/// </summary>
public sealed partial class RunsByActivityViewModel : ViewModelBase
{
    private readonly IActivityStatsSource? _source;
    private readonly ActivityStatsLabels _labels;
    private readonly Func<RunTypeId, bool> _passesType;
    private readonly TimeProvider _time;
    private readonly LootTableAccess? _loot;
    private IReadOnlyList<ActivityStatsRowViewModel> _all = [];
    private CancellationTokenSource? _reading;
    private ActivityStatsKey? _selectedKey;

    /// <param name="passesType">The TYPES tiles of the screen: whether an activity of this type is shown.</param>
    public RunsByActivityViewModel(IActivityStatsSource? source, ActivityStatsLabels labels, Func<RunTypeId, bool> passesType,
        ISharedServerPicks? picks, Func<string, DateTime, Task>? openRun, TimeProvider? time = null, LootTableAccess? loot = null)
    {
        _loot = loot;
        _source = source;
        _labels = labels;
        _passesType = passesType;
        _time = time ?? TimeProvider.System;
        Detail = new ActivityStatsDetailViewModel(picks, openRun, _time, loot);
    }

    /// <summary>The rows as the list draws them: filtered, then ordered.</summary>
    public ObservableCollection<ActivityStatsRowViewModel> Items { get; } = [];

    public ActivityStatsDetailViewModel Detail { get; }

    /// <summary>The LOOT column and its head: only where a loot table is registered.</summary>
    public bool HasLoot => _loot is not null;

    /// <summary>Raised when the reader picks a row, so the screen can open the drawer where the pane is not beside the list.</summary>
    public event Action<ActivityStatsRowViewModel>? RowSelected;

    /// <summary>What the list's own <c>SelectedItem</c> is bound to.</summary>
    [ObservableProperty] private ActivityStatsRowViewModel? _selectedRow;

    [ObservableProperty] private ActivityStatsSort _sort = ActivityStatsSort.LastFlown;
    [ObservableProperty] private bool _isDescending = true;
    [ObservableProperty] private bool _isLoading;

    /// <summary>Why the list is empty, or short: a server that cannot be read says so; an empty range says that.</summary>
    [ObservableProperty] private string _statusText = string.Empty;

    /// <summary>The filter ET-518's search sets; null shows everything. Applied to every row on top of the TYPES tiles.</summary>
    public Func<ActivityStatsRowViewModel, bool>? SearchFilter { get; set; }

    /// <summary>"3 of 12" while a search narrows the list; empty otherwise.</summary>
    [ObservableProperty] private string _countText = string.Empty;

    /// <summary>What every row of the list is searched on, for the suggestions under the search box.</summary>
    public IReadOnlyList<ActivitySearchProfile> Profiles => [.. _all.Select(row => row.Label.Search)];

    public bool HasStatus => StatusText.Length > 0;

    /// <summary>The YOURS column: the pilot's own runs, which a server tab has none of.</summary>
    [ObservableProperty] private bool _showsYoursColumn = true;

    /// <summary>Nothing to list and nothing being said about why: the empty range or the filters say so instead.</summary>
    public bool HasNoRows => Items.Count == 0 && !IsLoading && !HasStatus;

    public string EmptyText => TotalCount == 0
        ? "No activities with earnings figures in this range. Mining, freeruns and activities without a site have none."
        : "No activity matches the current filters.";

    public string LastFlownMark => _MarkOf(ActivityStatsSort.LastFlown);

    public string TypicalMark => _MarkOf(ActivityStatsSort.Typical);

    public string PerHourMark => _MarkOf(ActivityStatsSort.PerHour);

    public string RunsMark => _MarkOf(ActivityStatsSort.Runs);

    /// <summary>The rows every filter lets through, for the range line's count and for ET-518's "n of m".</summary>
    public int TotalCount => _all.Count;

    /// <summary>Reads the figures for what the screen shows and draws them. A read that is overtaken by the next one is
    /// dropped whole: its rows never land on a screen that has moved on.</summary>
    public async Task ShowAsync(ActivityStatsRequest request)
    {
        _reading?.Cancel();
        _reading?.Dispose();
        var reading = _reading = new CancellationTokenSource();
        if (_source is null)
        {
            _Replace([], null);
            return;
        }

        IsLoading = true;
        ShowsYoursColumn = request.ServerAddress is null;
        try
        {
            DateTime nowUtc = _time.GetUtcNow().UtcDateTime;
            (IReadOnlyList<ActivityStatsRowViewModel> rows, string? status) = await Task.Run(async () =>
            {
                ActivityStatsOverview overview = await _source.ReadAsync(request, reading.Token);
                return ((IReadOnlyList<ActivityStatsRowViewModel>)
                    [.. overview.Activities.Select(stats => new ActivityStatsRowViewModel(stats, _labels.For(stats.Key, stats.SiteName), nowUtc, _loot))],
                    overview.Status);
            }, reading.Token);
            if (reading.IsCancellationRequested)
                return;

            _Replace(rows, status);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_reading, reading))
                IsLoading = false;
        }
    }

    /// <summary>The filters changed: the same rows, drawn again.</summary>
    public void Reapply() => _Draw();

    /// <summary>Selects an activity by key — from the run's "Compared to typical" line — and shows its detail. A key the list
    /// does not hold (another month, a filter) is read on its own and shown without a row in the list.</summary>
    public void SelectKey(ActivityStatsKey key, ActivityStats read)
    {
        _selectedKey = key;
        ActivityStatsRowViewModel? row = _all.FirstOrDefault(known => known.Key == key);
        row ??= new ActivityStatsRowViewModel(read, _labels.For(read.Key, read.SiteName), _time.GetUtcNow().UtcDateTime, _loot);

        SelectedRow = Items.Contains(row) ? row : null;
        Detail.Show(row);
        RowSelected?.Invoke(row);
    }

    /// <summary>↑ and ↓ over the rows as the list draws them, with nothing selected yet the first (or last) one.</summary>
    public void MoveSelection(int direction)
    {
        if (Items.Count == 0 || direction == 0)
            return;

        int from = SelectedRow is null ? -1 : Items.IndexOf(SelectedRow);
        int index = Math.Clamp(from < 0 ? (direction > 0 ? 0 : Items.Count - 1) : from + Math.Sign(direction), 0, Items.Count - 1);
        SelectedRow = Items[index];
    }

    [RelayCommand]
    private void SortByLastFlown() => _SortBy(ActivityStatsSort.LastFlown);

    [RelayCommand]
    private void SortByTypical() => _SortBy(ActivityStatsSort.Typical);

    [RelayCommand]
    private void SortByPerHour() => _SortBy(ActivityStatsSort.PerHour);

    [RelayCommand]
    private void SortByRuns() => _SortBy(ActivityStatsSort.Runs);

    /// <summary>A click on a row. The same row again still asks for its pane: it is how a closed drawer opens again.</summary>
    public void Select(ActivityStatsRowViewModel row)
    {
        if (!ReferenceEquals(SelectedRow, row))
        {
            SelectedRow = row;
            return;
        }

        Detail.Show(row);
        RowSelected?.Invoke(row);
    }

    partial void OnSelectedRowChanged(ActivityStatsRowViewModel? value)
    {
        if (value is null)
            return;

        _selectedKey = value.Key;
        Detail.Show(value);
        RowSelected?.Invoke(value);
    }

    partial void OnStatusTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasStatus));
        OnPropertyChanged(nameof(HasNoRows));
    }

    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(HasNoRows));

    private void _SortBy(ActivityStatsSort sort)
    {
        // The same column again reverses it; another starts from the end that shows the most first.
        IsDescending = Sort == sort ? !IsDescending : true;
        Sort = sort;
        _Draw();
        OnPropertyChanged(nameof(LastFlownMark));
        OnPropertyChanged(nameof(TypicalMark));
        OnPropertyChanged(nameof(PerHourMark));
        OnPropertyChanged(nameof(RunsMark));
    }

    private string _MarkOf(ActivityStatsSort sort) => Sort != sort ? string.Empty : IsDescending ? " ▾" : " ▴";

    private void _Replace(IReadOnlyList<ActivityStatsRowViewModel> rows, string? status)
    {
        foreach (ActivityStatsRowViewModel row in rows)
            row.SelectRequested += Select;
        _all = rows;
        StatusText = status ?? string.Empty;
        _Draw();
        OnPropertyChanged(nameof(TotalCount));
    }

    private void _Draw()
    {
        ActivityStatsRowViewModel[] shown = [.. _all.Where(row => _passesType(row.Label.TypeId) && (SearchFilter?.Invoke(row) ?? true))];
        Items.ReconcileTo(_Ordered(shown));
        CountText = SearchFilter is null ? string.Empty : $"{Items.Count} of {TotalCount}";
        OnPropertyChanged(nameof(HasNoRows));
        OnPropertyChanged(nameof(EmptyText));

        // The selection follows its key into the new rows; the detail already on screen stays for a key that left the list.
        ActivityStatsRowViewModel? same = _selectedKey is { } key ? Items.FirstOrDefault(row => row.Key == key) : null;
        if (same is not null && !ReferenceEquals(same, SelectedRow))
        {
            SelectedRow = same;
            return;
        }

        if (same is null && SelectedRow is not null)
            SelectedRow = null;
    }

    /// <summary>A row without the figure sorted on goes below every row that has one, whichever way it runs: "—" is not
    /// the smallest number. They keep the order of most runs behind them, then the name.</summary>
    private List<ActivityStatsRowViewModel> _Ordered(IReadOnlyList<ActivityStatsRowViewModel> rows)
    {
        Func<ActivityStatsRowViewModel, IComparable?> value = Sort switch
        {
            ActivityStatsSort.Typical => row => row.TypicalValue,
            ActivityStatsSort.PerHour => row => row.PerHourValue,
            ActivityStatsSort.Runs => row => row.RunsValue,
            _ => row => row.LastFlownUtc
        };
        ActivityStatsRowViewModel[] sortable = [.. rows.Where(row => value(row) is not null)];
        IOrderedEnumerable<ActivityStatsRowViewModel> ordered = IsDescending
            ? sortable.OrderByDescending(row => value(row))
            : sortable.OrderBy(row => value(row));
        return
        [
            .. ordered.ThenBy(row => row.Label.SearchText, StringComparer.OrdinalIgnoreCase),
            .. rows.Except(sortable)
                .OrderByDescending(row => row.RunsValue ?? 0d)
                .ThenBy(row => row.Label.SearchText, StringComparer.OrdinalIgnoreCase)
        ];
    }
}
