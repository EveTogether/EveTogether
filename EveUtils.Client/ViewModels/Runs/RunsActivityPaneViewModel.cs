using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;
using EveUtils.Client.Formatting;
using EveUtils.Client.Runs;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Earnings;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Isk;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>What one detail read brought back for the pane: the pilots' own runs, and what the loot lines add up to.</summary>
/// <param name="Status">Why there is nothing, when there is nothing — a failed read is a state, not silence.</param>
public sealed record RunsPaneDetail(
    IReadOnlyList<ActivityRunRowViewModel> Crew, int LootItemCount, decimal? LootIsk, string? Status);

/// <summary>How a run stands against what its activity typically earns (ET-517), worded for the pane: "Compared to typical:
/// +12M (top 25 %)". <paramref name="Key"/> is the activity whose detail the line opens.</summary>
public sealed record RunComparison(ActivityStatsKey Key, string Text)
{
    /// <summary>Null where the figures have no typical run: below the minimum there is nothing to compare with.</summary>
    public static RunComparison? Of(ActivityStatsKey key, decimal yield, EarningsFiguresDto figures)
    {
        if (RunVsTypical.Of(yield, figures) is not { } against)
            return null;

        string standing = against.Band switch
        {
            RunBand.Top => " (top 25 %)",
            RunBand.Bottom => " (bottom 25 %)",
            _ => string.Empty
        };
        return new RunComparison(key,
            $"Compared to typical: {(against.Difference >= 0 ? "+" : string.Empty)}{IskFormat.Compact(against.Difference)}{standing}");
    }
}

/// <summary>The saved activity whose position in its activity (ET-520) the pane shows, and the pilot's characters that read it.</summary>
public sealed record RunPositionTarget(string ActivityRef, IReadOnlyList<int> CharacterIds);

/// <summary>One line of the ISK section: the source's own ink, its name and its whole figure.</summary>
/// <param name="Ink">A breakdown of this source alone, so the swatch is the same <c>IskSourceBar</c> the row and the
/// day header draw — one place decides what a source's colour is. Null for consumables, which the bar leaves out on
/// purpose: it is money spent, and a bar of what came in has no negative part to draw it as.</param>
public sealed record RunsIskPartViewModel(string Label, IskBreakdown? Ink, string AmountText)
{
    public bool HasInk => Ink is not null;
}

/// <summary>
/// The selected run, read beside the list at 1303 and in the drawer at 758 (ET-291) — one view model, two hosts, of
/// which one is shown. Not the ET-162 detail screen's own sections: MINING's 382 px columns, FLEET's non-wrapping row
/// of figures and that header's two 26 px figures are drawn for 758–1040 and do not fit 400, so this is a compact view
/// of its own. OPEN DETAIL is still here for everything it leaves out.
///
/// <para>The head, the hero figure and the whole ISK section come straight off the row's own DTO and are on screen the
/// moment a row is clicked. Only CREW, ENEMIES and LOOT need the detail query — eight to ten round trips and a market
/// lookup — so that read runs off the UI thread, after a short wait, and is dropped when the selection has moved on:
/// ten quick arrow steps are one read, not ten (ET-287).</para>
///
/// <para>SUMMARY (<see cref="RunsSummaryViewModel"/>, ET-294) takes the same two hosts and stands in this one's place
/// while it is chosen, or while no run is selected on the wide layout.</para>
/// </summary>
public sealed partial class RunsActivityPaneViewModel : ViewModelBase
{
    /// <summary>Long enough that holding ↓ down reads once at the end of it, short enough that a single click never
    /// feels like it is waiting for anything.</summary>
    public static readonly TimeSpan ReadDelay = TimeSpan.FromMilliseconds(150);

    internal static readonly (IskSource Source, string Label)[] Sources =
    [
        (IskSource.Bounty, "BOUNTY"),
        (IskSource.Loot, "LOOT"),
        (IskSource.HomefrontPayout, "HOMEFRONT PAYOUT"),
        (IskSource.Rewards, "MISSION REWARDS"),
        (IskSource.Mining, "MINING")
    ];

    private readonly Func<ActivityOverviewRowViewModel, CancellationToken, Task<RunsPaneDetail>> _readDetail;
    private readonly Func<string?> _publishTargetName;
    private readonly TimeSpan _readDelay;
    private readonly Func<ActivityOverviewRowViewModel, CancellationToken, Task<RunComparison?>>? _compare;
    private readonly Action<ActivityStatsKey>? _openStats;
    private readonly Func<ActivityOverviewRowViewModel, RunPositionTarget?>? _positionTarget;
    private CancellationTokenSource? _reading;
    private RunComparison? _comparison;

    /// <param name="readDetail">The activity's runs and loot, read off the UI thread by whoever owns the dispatcher.</param>
    /// <param name="publishTargetName">The coupled server PUBLISH would send to, named as its tab names it; null when
    /// none is coupled or several are, which the button's own text then leaves unnamed.</param>
    /// <param name="readDelay">Zero in a test that wants the read to have happened by the time it looks.</param>
    /// <param name="compare">How the run stands against the typical run of its activity (ET-517), read after the detail; null
    /// leaves the line out.</param>
    /// <param name="openStats">Opens an activity's earnings in the overview's BY ACTIVITY detail.</param>
    /// <param name="position">The run's place among the runs of its activity (ET-520); null leaves the block out.</param>
    /// <param name="positionTarget">Which saved activity a row is, or null for a row that is not the pilot's own take.</param>
    public RunsActivityPaneViewModel(
        Func<ActivityOverviewRowViewModel, CancellationToken, Task<RunsPaneDetail>> readDetail,
        Func<string?>? publishTargetName = null,
        TimeSpan? readDelay = null,
        Func<ActivityOverviewRowViewModel, CancellationToken, Task<RunComparison?>>? compare = null,
        Action<ActivityStatsKey>? openStats = null,
        RunPositionViewModel? position = null,
        Func<ActivityOverviewRowViewModel, RunPositionTarget?>? positionTarget = null)
    {
        Position = position;
        _positionTarget = positionTarget;
        _compare = compare;
        _openStats = openStats;
        _readDetail = readDetail;
        _publishTargetName = publishTargetName ?? (() => null);
        _readDelay = readDelay ?? ReadDelay;
    }

    /// <summary>The row on show, and the one every action here applies to. Bound to directly for PUBLISH, RETRY and
    /// OPEN DETAIL rather than mirrored into commands of this pane's own: two copies of one command is how the pane
    /// and the row end up disagreeing about whether an activity can be published.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActivity))]
    private ActivityOverviewRowViewModel? _row;

    public bool HasActivity => Row is not null;

    // ── Head, straight off the row ──────────────────────────────────────────────────────────────────────────────

    [ObservableProperty] private MaterialIconKind _typeIcon = MaterialIconKind.HelpCircleOutline;

    [ObservableProperty] private string _typeText = string.Empty;

    [ObservableProperty] private string _siteText = string.Empty;

    /// <summary>"SUN 13 SEP 21:52 · Alkabsi 0.7 · NKP-364" — the day and time, the system, and the signature only
    /// where one was scanned.</summary>
    [ObservableProperty] private string _metaText = string.Empty;

    [ObservableProperty] private bool _hasAutoSavedRun;

    [ObservableProperty] private bool _hasSyncState;

    [ObservableProperty] private string _syncText = string.Empty;

    [ObservableProperty] private MaterialIconKind _syncIcon = MaterialIconKind.CloudCheckOutline;

    [ObservableProperty] private bool _hasNet;

    [ObservableProperty] private string _netText = string.Empty;

    [ObservableProperty] private string _noNetText = string.Empty;

    [ObservableProperty] private string? _noNetTooltip;

    [ObservableProperty] private IskBreakdown _isk = IskBreakdown.None;

    [ObservableProperty] private string _durationText = string.Empty;

    // ── ISK ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The same total the hero figure compacts, written out in full — 76,350,000 ISK beside +76.35M.</summary>
    [ObservableProperty] private string _iskWholeText = string.Empty;

    public ObservableCollection<RunsIskPartViewModel> IskParts { get; } = [];

    [ObservableProperty] private bool _hasIskParts;

    public ObservableCollection<ActivityRewardChipViewModel> Chips { get; } = [];

    [ObservableProperty] private bool _hasChips;

    // ── Crew, enemies, loot: the detail read ────────────────────────────────────────────────────────────────────

    public ObservableCollection<ActivityRunRowViewModel> Crew { get; } = [];

    [ObservableProperty] private string _crewSummaryText = string.Empty;

    [ObservableProperty] private string? _crewStatus;

    [ObservableProperty] private string _enemiesSummaryText = string.Empty;

    [ObservableProperty] private string _enemiesText = string.Empty;

    [ObservableProperty] private bool _hasLoot;

    [ObservableProperty] private string _lootSummaryText = string.Empty;

    // ── Compared to typical (ET-517) ────────────────────────────────────────────────────────────────────────────

    /// <summary>"Compared to typical: +12M (top 25 %)", empty until the comparison is read and where the activity has no
    /// typical run yet. A click opens the activity's earnings.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasComparison))]
    private string _comparisonText = string.Empty;

    public bool HasComparison => ComparisonText.Length > 0;

    [RelayCommand]
    private void OpenStats()
    {
        if (_comparison is { } comparison)
            _openStats?.Invoke(comparison.Key);
    }

    /// <summary>Where the run stands among the runs of its activity (ET-520): the panel of the run window over this run,
    /// its rank and its notable drop. Filled after the pane is up, never before it: opening the pane waits for none of it.</summary>
    public RunPositionViewModel? Position { get; }

    private void _ShowPosition(ActivityOverviewRowViewModel? row)
    {
        if (Position is null)
            return;

        if (row is not null && _positionTarget?.Invoke(row) is { } target)
            _ = Position.ShowAsync(target.ActivityRef, target.CharacterIds, _readDelay);
        else
            Position.Clear();
    }

    // ── Actions ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>"PUBLISH TO ET" where exactly one server is coupled, the bare verb where the target is a choice.</summary>
    [ObservableProperty] private string _publishText = "PUBLISH";

    /// <summary>
    /// Show this run, or nothing. Everything the row already knows is set here and now; the rest is read after
    /// <see cref="ReadDelay"/> and only if the selection has not moved on by then.
    /// </summary>
    public void Show(ActivityOverviewRowViewModel? row)
    {
        // Cancelled before anything else: a read still out for the previous row must not land on this one.
        _reading?.Cancel();
        _reading?.Dispose();
        _reading = null;

        Row = row;
        _comparison = null;
        ComparisonText = string.Empty;
        if (row is null)
        {
            Crew.Clear();
            CrewStatus = null;
            _ShowPosition(null);
            return;
        }

        _ShowHead(row);
        _ShowIsk(row);
        _ShowWaitingForDetail(row);
        _ShowPosition(row);

        var reading = new CancellationTokenSource();
        _reading = reading;
        _ = _ReadDetailAsync(row, reading.Token);
    }

    /// <summary>The same read again for the run on show — after a change reached it (ET-222), without the head
    /// flickering through an empty state it never left.</summary>
    public void RereadDetail()
    {
        if (Row is not { } row)
            return;

        _reading?.Cancel();
        _reading?.Dispose();
        var reading = new CancellationTokenSource();
        _reading = reading;
        _ = _ReadDetailAsync(row, reading.Token);
        _ShowPosition(row);
    }

    private void _ShowHead(ActivityOverviewRowViewModel row)
    {
        TypeIcon = row.TypeIcon;
        TypeText = row.TypeText;
        SiteText = row.SiteText;
        MetaText = string.Join(" · ", new[]
        {
            row.StartedAtLocal.ToString("ddd d MMM HH:mm", CultureInfo.InvariantCulture).ToUpperInvariant(),
            row.HasSystem ? row.SystemLineText : null,
            row.ScanSignatureText
        }.OfType<string>());
        HasAutoSavedRun = row.HasAutoSavedRun;
        HasSyncState = row.HasSyncText;
        SyncText = row.SyncText;
        SyncIcon = row.SyncIcon;

        HasNet = row.HasNet;
        NetText = row.NetText;
        NoNetText = row.NoNetText;
        NoNetTooltip = row.NoNetTooltip;
        Isk = row.Isk;
        DurationText = row.DurationText;
        PublishText = _publishTargetName() is { Length: > 0 } server
            ? $"PUBLISH TO {server.ToUpperInvariant()}"
            : "PUBLISH";
    }

    private void _ShowIsk(ActivityOverviewRowViewModel row)
    {
        IskWholeText = row.Isk.HasFigure ? IskFormat.Whole(row.Isk.Total) : "nothing recorded to value";
        List<RunsIskPartViewModel> parts = [.. Sources
            .Select(source => (source.Label, Part: row.Isk.Of(source.Source)))
            .Where(line => line.Part is { Certainty: not IskCertainty.Unknown, Amount: not 0 })
            .Select(line => new RunsIskPartViewModel(line.Label,
                new IskBreakdown([line.Part!]), IskFormat.Whole(line.Part!.Amount)))];
        // Spent, not earned, and so not in the bar above — but a total that is short by it without saying why reads
        // as a wrong total. Named as its own line with the minus it carries, and the sum stays the sum (ET-256).
        if (row.Isk.Of(IskSource.Consumables) is { Certainty: not IskCertainty.Unknown, Amount: not 0 } consumables)
            parts.Add(new RunsIskPartViewModel("CONSUMABLES", null, IskFormat.Whole(consumables.Amount)));
        if (row.Isk.Of(IskSource.ShipLoss) is { Certainty: not IskCertainty.Unknown, Amount: not 0 } shipLoss)
        {
            parts.Add(new RunsIskPartViewModel("SHIP LOSS", null, IskFormat.Whole(shipLoss.Amount)));
        }

        IskParts.ReconcileTo(parts);
        HasIskParts = parts.Count > 0;
        Chips.ReconcileTo([.. row.Chips]);
        HasChips = Chips.Count > 0;
    }

    /// <summary>What the three read-backed sections say before the read lands: the row's own counts, which are right,
    /// rather than an empty table that would read as "nobody flew it".</summary>
    private void _ShowWaitingForDetail(ActivityOverviewRowViewModel row)
    {
        Crew.Clear();
        CrewStatus = null;
        CrewSummaryText = row.CrewCount <= 1 ? "solo" : $"{row.CrewCount} pilots";
        EnemiesSummaryText = row.EnemyTypeCount > 0 ? $"{row.EnemyTypeCount} types" : string.Empty;
        EnemiesText = row.EnemyTypeCount > 0
            ? $"{row.EnemyTypeCount} enemy types observed on this activity. The full count is in the detail screen."
            : "No enemies counted.";
        HasLoot = false;
        LootSummaryText = string.Empty;
    }

    private async Task _ReadDetailAsync(ActivityOverviewRowViewModel row, CancellationToken cancellationToken)
    {
        try
        {
            if (_readDelay > TimeSpan.Zero)
                await Task.Delay(_readDelay, cancellationToken);

            RunsPaneDetail detail = await _readDetail(row, cancellationToken);
            // Two guards, not one: the token catches a selection that moved while the query was out, the row catches
            // a reply that raced its own cancellation.
            if (cancellationToken.IsCancellationRequested || !ReferenceEquals(Row, row))
                return;

            for (int index = 0; index < detail.Crew.Count; index++)
                detail.Crew[index].IsAlternate = index % 2 == 1;
            Crew.ReconcileTo(detail.Crew);
            CrewStatus = detail.Status;
            if (detail.Crew.Count > 0)
                CrewSummaryText = detail.Crew.Count == 1 ? "solo" : $"{detail.Crew.Count} pilots";
            HasLoot = detail.LootItemCount > 0;
            LootSummaryText = HasLoot
                ? $"{detail.LootItemCount} items · {(detail.LootIsk is { } loot ? IskFormat.Compact(loot) : "—")}"
                : string.Empty;

            if (_compare is null)
                return;

            RunComparison? comparison = await _compare(row, cancellationToken);
            if (cancellationToken.IsCancellationRequested || !ReferenceEquals(Row, row))
                return;

            _comparison = comparison;
            ComparisonText = comparison?.Text ?? string.Empty;
        }
        catch (OperationCanceledException)
        {
            // The selection moved on. Whatever replaced it is already showing its own head.
        }
    }
}
