using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Formatting;
using EveUtils.Client.Runs;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Earnings;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.Extensions.Logging;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>
/// Where a saved run stands among the runs of its activity (ET-520): the run window's EXPECTED EARNINGS panel with the
/// run's own ISK as the marker, the rank among the other runs, and, when the run strayed above the usual range, the
/// drop that made it. Shown in the run details and in the runs list's drawer.
///
/// <para>Opening costs nothing: the block shows "looking up" at once and the figures are read off the UI thread, from
/// the caches <see cref="IRunPositionSource"/> keeps. A read belongs to its run: moving on to another cancels it, so a
/// late answer never lands on the wrong run.</para>
/// </summary>
public sealed partial class RunPositionViewModel : ViewModelBase
{
    private const double TopShareLimit = 0.25;

    private readonly IRunPositionSource _source;
    private readonly ILootTableOpener? _lootTables;
    private readonly ILogger<RunPositionViewModel>? _logger;
    private CancellationTokenSource? _reading;
    private RunPosition? _position;
    private IReadOnlyList<int> _characterIds = [];

    public RunPositionViewModel(IRunPositionSource source, ILootTableOpener? lootTables = null, ISharedServerPicks? picks = null,
        ILogger<RunPositionViewModel>? logger = null)
    {
        _source = source;
        _lootTables = lootTables;
        _logger = logger;
        Earnings = new ExpectedEarningsViewModel(null, null, picks, lootTables);
        Earnings.ServerChosen += _OnServerChosen;
    }

    /// <summary>The run window's panel, laid out the same way.</summary>
    public ExpectedEarningsViewModel Earnings { get; }

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasRankNote))] private string _rankNoteText = string.Empty;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasOutlier))] private string _outlierText = string.Empty;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasTopDrop))] private string _topDropText = string.Empty;
    [ObservableProperty] private string _topDropMetaText = string.Empty;

    public bool HasRankNote => RankNoteText.Length > 0;
    public bool HasOutlier => OutlierText.Length > 0;
    public bool HasTopDrop => TopDropText.Length > 0;

    /// <summary>The link from the notable drop to the loot table of the activity.</summary>
    public bool IsLootTableShown => _lootTables is not null && _position is not null;

    [RelayCommand]
    private void OpenLootTable()
    {
        if (_position is { } position)
            _lootTables?.Open(position.Run.Key, _characterIds);
    }

    /// <summary>Points the block at a saved activity. <paramref name="delay"/> keeps a held arrow key from reading for
    /// every row it passes over.</summary>
    public async Task ShowAsync(string activityRef, IReadOnlyList<int> characterIds, TimeSpan delay = default)
    {
        _reading?.Cancel();
        _reading?.Dispose();
        CancellationTokenSource reading = new();
        _reading = reading;
        _characterIds = characterIds;
        _SetPosition(null);
        Earnings.BeginLoading();
        try
        {
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, reading.Token);

            RunPosition? position = await Task.Run(() => _source.ReadAsync(activityRef, characterIds, reading.Token), reading.Token);
            if (reading.IsCancellationRequested)
                return;

            if (position is null)
            {
                Earnings.Clear();
                return;
            }

            _Present(position);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _logger?.LogError(exception, "The position of {Activity} in its activity could not be read", activityRef);
            if (!reading.IsCancellationRequested)
                Clear();
        }
    }

    /// <summary>No run: hides the block and drops a read still out.</summary>
    public void Clear()
    {
        _reading?.Cancel();
        _SetPosition(null);
        Earnings.Clear();
    }

    private void _Present(RunPosition position)
    {
        _SetPosition(position);
        Earnings.Present(position.Run.Key, _characterIds.Count > 0 ? _characterIds[0] : null, position.Basis, position.Reason,
            position.Answers, position.Run.YieldIsk);
        _RenderStanding(position);
    }

    // The pilot switched the panel to another server's figures: the run is ranked among those instead.
    private void _OnServerChosen(ExpectedEarnings answer)
    {
        if (_position is not { } position)
            return;

        _SetPosition(RunPosition.From(position.ActivityRef, position.Run, answer, position.Answers, OwnRunsReason.None));
        _RenderStanding(_position ?? position);
    }

    private void _SetPosition(RunPosition? position)
    {
        _position = position;
        OnPropertyChanged(nameof(IsLootTableShown));
        if (position is null)
            _ClearStanding();
    }

    private void _ClearStanding()
    {
        Earnings.SetRank(string.Empty);
        RankNoteText = OutlierText = TopDropText = TopDropMetaText = string.Empty;
    }

    private void _RenderStanding(RunPosition position)
    {
        _ClearStanding();
        EarningsFiguresDto figures = position.Basis.Figures;
        if (figures.Typical is null)
            return;

        _RenderRank(position, figures);
        _RenderOutlier(position, figures);
    }

    private void _RenderRank(RunPosition position, EarningsFiguresDto figures)
    {
        if (position.Rank is not { } rank)
        {
            RankNoteText = position.Run.IsCounted
                ? "Not enough other runs to rank this one yet."
                : "This run does not count towards the figures (under a minute, or no loot counted), so it has no rank.";
            return;
        }

        double above = 1 - rank.Percentile;
        string top = above <= TopShareLimit ? $" · top {Math.Max(1, (int)Math.Ceiling(above * 100)).ToString(CultureInfo.InvariantCulture)}%" : string.Empty;
        Earnings.SetRank($" · better than {(int)Math.Floor(rank.Percentile * 100)}%{top}");
    }

    private void _RenderOutlier(RunPosition position, EarningsFiguresDto figures)
    {
        bool deciles = figures.Band is EarningsBand.Deciles;
        decimal low = (deciles ? figures.P10 : figures.P25) ?? 0;
        decimal high = (deciles ? figures.P90 : figures.P75) ?? decimal.MaxValue;
        string range = deciles ? "8 of 10 runs" : "half of runs";
        decimal yield = position.Run.YieldIsk;
        if (yield < low)
        {
            OutlierText = $"Below the range of {range}";
            return;
        }

        if (yield <= high)
            return;

        OutlierText = $"Above the range of {range}";
        if (position.Run.TopDrop is not { } drop)
            return;

        TopDropText = drop.Quantity > 1 ? $"{drop.Name} ×{drop.Quantity.ToString("N0", CultureInfo.InvariantCulture)}" : drop.Name;
        List<string> meta = [];
        if (drop.Rarity is not LootRarityClass.Common)
            meta.Add(drop.Rarity.ToString().ToLowerInvariant());
        meta.Add($"{IskFormat.Compact(drop.ValueIsk)} ISK");
        if (yield > 0)
            meta.Add($"{Math.Min(100, (int)Math.Floor(drop.ValueIsk / yield * 100)).ToString(CultureInfo.InvariantCulture)}% of this run");
        TopDropMetaText = string.Join(" · ", meta);
    }
}
