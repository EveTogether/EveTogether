using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Calendar;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Esi;
using EveUtils.Client.Fleet;
using EveUtils.Client.Imaging;
using EveUtils.Client.Messaging;
using EveUtils.Client.Notifications;
using EveUtils.Client.Platform;
using EveUtils.Client.Runs;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Esi.Http;
using EveUtils.Shared.Modules.Market.Services;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Isk;
using EveUtils.Shared.Modules.Runs.Queries;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Transport;
using Microsoft.Extensions.DependencyInjection;
using CqrsDispatcher = EveUtils.Shared.Cqrs.IDispatcher;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>
/// ET-161: the runs screen in the shell — RUNNING on top, then one row per finished activity under its day. Until this
/// existed a saved run left the screen the moment its window closed and there was nowhere in the app to see what you
/// flew yesterday.
///
/// <para><b>One compact list, drawn from the space it is handed (ET-290).</b> <c>ModuleHostService.Render</c> moves this
/// very <c>Content</c> between a docked tab and a floating window, so the layout sizes off its host and never off the
/// window: docked at 1303 and floating at 758 are the same list with more or less room for a site's name. Every row has
/// one fixed height at every width; what does not fit is trimmed, chips included, and the activity pane (ET-291) is where
/// all of it can be read. The day headers, activity rows and pilots' runs are one flat, virtualised sequence per tab
/// (<see cref="RunsTabViewModel.Items"/>): folding a day takes its rows out of it, since expanders nested inside a list
/// would put every row back in the visual tree.</para>
///
/// <para><b>Wider is not a second design (ET-291).</b> The same list and the same pane at both widths: at and above
/// <see cref="RunsLayout.WideFrom"/> the pane stands beside the list in a column of its own, below it the very same
/// pane slides in over the list as a 430 px drawer, with the list dimmed behind it and a click on it to close. What
/// changes is where the pane is drawn, never what it says.</para>
///
/// <para><b>The selection is this screen's, not the list's.</b> It hangs on <c>ActivitySummaryId</c> and survives a
/// refresh that replaces the row instance, a filter, the drawer closing, and — the case a <c>ListBox</c> cannot hold —
/// its own day being folded: a folded day takes its rows out of <see cref="RunsTabViewModel.Items"/>, at which point
/// the list drops its <c>SelectedItem</c>. That null is never read as "nothing is selected" (ET-290 + ET-291).</para>
///
/// <para><b>Nothing is read on the UI thread.</b> Every query and command this screen sends runs inside
/// <c>Task.Run</c>, with building the rows from what came back — a dispatcher call is a direct one and the SQLite
/// provider's async API is synchronous, so an <c>await</c> alone moved nothing. Only the collections change on the UI
/// thread, reconciled by identity (ET-222). One read runs at a time; a change that lands while one is out is owed and
/// read straight after, never dropped (ET-287).</para>
/// </summary>
public sealed partial class RunsOverviewViewModel : ViewModelBase, IRefreshableModule, IDisposable
{
    private readonly CqrsDispatcher _dispatcher;
    private readonly IDialogService _dialogs;
    private readonly IServiceProvider _services;
    private readonly IReadOnlyDictionary<long, string> _namesById;
    private readonly RunsCharacterNames _characterNames;
    private readonly DispatcherTimer? _clock;
    private readonly RunsFleetFilter? _fleetFilter;
    private readonly IDisposable? _runChangesSubscription;
    private readonly FleetRunAutoPublisher? _autoPublisher;
    private readonly RunRowFacts _facts;
    private readonly ConcurrentDictionary<long, CharacterFaceViewModel> _faces = new();
    private bool _canPublish;

    /// <summary>Set once per load when <see cref="_fleetFilter"/> is active and turned up nothing: whether that
    /// empty result is a real zero or an unknowable one (ET-185) — read by <see cref="_EmptyMessageFor"/> so every
    /// tab's empty message says the same thing rather than each guessing from its own row count.</summary>
    private bool _fleetHistoryKnownEmpty;

    /// <summary>The read in flight, if any. A change that lands meanwhile is owed to the next pass, which runs right
    /// after it and is awaited by whoever asked — so two reads never interleave on this screen, and none is lost.</summary>
    private TaskCompletionSource? _reading;
    private bool _isReadOwed;
    private RunChangeBatch? _owedChanges;
    private bool _owedAutoSave;

    /// <summary>The month on screen (ET-233), the first of that month at local midnight. Local, not UTC, because the
    /// days below it already group by local day (ET-98) — a month that disagreed with its own days about where
    /// midnight falls would put an activity under a header that says one date inside a month that says another.
    /// Untouched by a live refresh: only the range line's ◀▶, a month's name in the strip and a day or week picked in
    /// another month move it, so a run landing in the current month while an older one is on screen never pulls the
    /// reader back to it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MonthHeaderText))]
    private DateTime _viewedMonthLocal = _MonthStart(DateTime.Now);

    public string MonthHeaderText => ViewedMonthLocal.ToString("MMMM yyyy", CultureInfo.InvariantCulture).ToUpperInvariant();

    private readonly IWeekStartService? _weekStart;

    /// <summary>Every activity the last read brought, over the strip and the month in view together, as the strip
    /// counts them — rows are only ever built for the month (ET-292). What the range line totals a picked day or week
    /// from, so a week reaching into the month before is still counted whole.</summary>
    private IReadOnlyList<RunsActivityFacts> _loadedFacts = [];

    /// <summary>Every row the last read built for the month, before a tab's own server rule or the TYPES/CHARACTERS
    /// filter (ET-293) narrows it — what a tile toggle re-filters from, so hiding a type is as cheap as folding a day
    /// and never a read of its own.</summary>
    private IReadOnlyList<ActivityOverviewRowViewModel> _loadedRows = [];

    /// <summary>What each server tab last read from its server (ET-311), by address — rows for the month and facts for
    /// the strip, or why the read gave none. Read again only on a full read: a live change says nothing about a
    /// server, and a tab switch or a filter toggle re-shows what is here.</summary>
    private IReadOnlyDictionary<string, ServerTabRead> _serverReads = new Dictionary<string, ServerTabRead>();

    /// <summary>Types and characters a tile turned off. Empty means every tile is on, which this screen treats as no
    /// filter at all rather than "everything present is selected" — the difference matters for CHARACTERS: with
    /// nothing excluded, a group-mate's activity with none of this machine's own characters on it still shows (Jithran,
    /// 15 Sep); the moment one character goes off, only a row with an own, still-on character passes.</summary>
    private readonly HashSet<RunTypeId> _excludedTypes = [];

    private readonly HashSet<long> _excludedCharacters = [];

    private DateOnly _loadedFrom = DateOnly.MaxValue;
    private DateOnly _loadedTo = DateOnly.MinValue;
    private DateOnly? _firstTracked;

    /// <summary>The selected tab's activities per local day, as <see cref="_RefreshRange"/> last grouped them.</summary>
    private IReadOnlyDictionary<DateOnly, IReadOnlyList<RunsActivityFacts>> _tabDays =
        new Dictionary<DateOnly, IReadOnlyList<RunsActivityFacts>>();

    // ══ The range line and the activity strip (ET-292) ══════════════════════════════════════════════════════════

    public RunsActivityStripViewModel Strip { get; }

    // ══ TYPES and CHARACTERS filters (ET-293) ═══════════════════════════════════════════════════════════════════

    public RunFilterBlockViewModel TypeFilter { get; }

    public RunFilterBlockViewModel CharacterFilter { get; }

    /// <summary>The month in view, or a day or week picked in the strip. Picking never filters the list: the list is
    /// always the month, and the pick is what the range line totals and the strip outlines.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRangePicked))]
    private RunsRangeKind _rangeKind = RunsRangeKind.Month;

    /// <summary>The picked day, or the first day of the picked week. Meaningless while <see cref="RangeKind"/> is the
    /// month.</summary>
    [ObservableProperty] private DateOnly _rangeStart;

    public bool IsRangePicked => RangeKind != RunsRangeKind.Month;

    [ObservableProperty] private string _rangeTitleText = string.Empty;
    [ObservableProperty] private string _rangeCountText = string.Empty;
    [ObservableProperty] private string _rangeFlownText = string.Empty;
    [ObservableProperty] private string _rangeNetText = string.Empty;
    [ObservableProperty] private IskBreakdown _rangeIsk = IskBreakdown.None;
    [ObservableProperty] private string _previousTooltip = "Previous month";
    [ObservableProperty] private string _nextTooltip = "Next month";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviousCommand))]
    private bool _canGoPrevious = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    private bool _canGoNext = true;

    /// <summary>A row the summary's TOP RUNS opened (ET-294): its day is unfolded, and the row itself belongs in view
    /// once the list has laid that out.</summary>
    public event Action<ActivityOverviewRowViewModel>? RowScrollRequested;

    /// <summary>A day was unfolded for the reader and belongs at the top of the list — once the list has laid the
    /// change out, which only the view can tell (RunsWindow.axaml.cs).</summary>
    public event Action<RunsDayViewModel>? DayScrollRequested;

    /// <param name="paneReadDelay">How long the pane waits before reading a newly selected activity (ET-291); zero in
    /// a test that wants that read to have happened by the time it looks.</param>
    public RunsOverviewViewModel(CqrsDispatcher dispatcher, IDialogService dialogs, IServiceProvider services,
        IReadOnlyList<Character> characters, bool runClock = true, RunsFleetFilter? fleetFilter = null,
        TimeSpan? paneReadDelay = null)
    {
        _dispatcher = dispatcher;
        _dialogs = dialogs;
        _services = services;
        _fleetFilter = fleetFilter;
        Tabs = [new RunsTabViewModel("Local", null, _PublishDayAsync)];
        _facts = new RunRowFacts(services.GetService<ISdeAccessor>());
        _namesById = characters
            .Where(character => character.EsiCharacterId is > 0)
            .GroupBy(character => (long)character.EsiCharacterId!.Value)
            .ToDictionary(group => group.Key, group => group.First().Name);
        // Own characters first, then CachedExternalCharacter through the same cache-then-ESI path the external-member
        // flow already uses (ET-306) — never the bare id while a name is there to find anywhere.
        _characterNames = new RunsCharacterNames(_namesById, services.GetService<IExternalCharacterLookup>());
        Strip = new RunsActivityStripViewModel(
            day => _ = ToggleDayAsync(day), week => _ = ToggleWeekAsync(week), month => _ = _GoToMonthAsync(month));
        TypeFilter = new RunFilterBlockViewModel("TYPES", () => { _excludedTypes.Clear(); _AfterFilterChanged(); });
        CharacterFilter = new RunFilterBlockViewModel("CHARACTERS", () => { _excludedCharacters.Clear(); _AfterFilterChanged(); });
        // The week start is live (ET-297): the strip re-lays itself on a change, with no read unless the grid now
        // reaches further back than the last one did.
        _weekStart = services.GetService<IWeekStartService>();
        if (_weekStart is not null)
            _weekStart.Changed += _OnWeekStartChanged;
        SelectedTab = LocalTab;
        Pane = new RunsActivityPaneViewModel(_ReadPaneDetailAsync, _PublishTargetName, paneReadDelay);
        Summary = new RunsSummaryViewModel(_FaceOf, day => _ = PickDayAsync(day), line => _ = OpenSummaryRunAsync(line));
        // HOURS and DAYS shade the way the strip does, so they follow its ISK | runs switch.
        Strip.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RunsActivityStripViewModel.Shade))
                _ShowSummary();
        };
        _RefreshRange();
        _RefreshFilterTiles();
        _WatchItems(LocalTab);

        // A lane per local character, running or not. Each asks GetRunningRunsQuery (ET-203) which run is running for
        // ITS character, so two characters running at once are two running lanes — that query has no "exactly one"
        // rule to hit, unlike the single-run GetRunningRunQuery a run window uses to reopen (ET-130). The band draws
        // them as a line per running group and an avatar per idle character (ET-290).
        Lanes = [.. characters
            .Where(character => character.EsiCharacterId is > 0)
            .Select(character => new RunningLaneViewModel(character,
                _FaceOf(character.EsiCharacterId!.Value, character.Name), _ActOnLaneAsync))];
        IdleLanes = [.. Lanes];
        LanesEmptyText = Lanes.Count == 0
            ? "No character is linked yet, so there is no one to start a run for."
            : null;

        // One subscription for every way a run can change (ET-222). The feed hands the change over on the UI thread
        // and folds a burst of payouts into one batch, so all that is left here is what to read again.
        _runChangesSubscription = services.GetService<RunChangeFeed>()?.Subscribe(_RefreshAsync);
        // Where a fleet run's automatic publish stands (ET-245) — announced through the same feed, so no second
        // subscription: only read here when a row is built.
        _autoPublisher = services.GetService<FleetRunAutoPublisher>();

        if (!runClock)
            return;

        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += _OnClockTick;
        _clock.Start();
    }

    /// <summary>Local first, then one tab per coupled server — the fit browser's strip, same sources, additive so a
    /// server coupled while this screen is open gets a tab without the others being rebuilt under the reader.</summary>
    public ObservableCollection<RunsTabViewModel> Tabs { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLocalTabSelected))]
    [NotifyPropertyChangedFor(nameof(ShowUnfinishedBand))]
    [NotifyPropertyChangedFor(nameof(LocalInViewCount))]
    [NotifyPropertyChangedFor(nameof(ShowPublishView))]
    [NotifyPropertyChangedFor(nameof(PublishViewButtonText))]
    private RunsTabViewModel? _selectedTab;

    /// <summary>RUNNING and UNFINISHED show only here. A running run is a clock on this machine and an unfinished run
    /// is a decision owed on it; neither is something a server holds, so neither belongs under a server's name.</summary>
    public bool IsLocalTabSelected => SelectedTab?.IsLocal ?? true;

    /// <summary>Whether there is anything to choose between. A lone "Local" tab is a label for a choice that does not
    /// exist, so the strip stays away until a server is coupled — the fit browser's rule.</summary>
    [ObservableProperty] private bool _hasServerTabs;

    private RunsTabViewModel LocalTab => Tabs[0];

    /// <summary>Every local character's place in RUNNING, running or not.</summary>
    public ObservableCollection<RunningLaneViewModel> Lanes { get; }

    /// <summary>A line per running group this machine's characters are on (<c>GroupCode ?? RunId</c>), earliest
    /// start first.</summary>
    public ObservableCollection<RunningGroupViewModel> RunningGroups { get; } = [];

    /// <summary>The characters with nothing running — an avatar each, one click from a start fixed on them.</summary>
    public ObservableCollection<RunningLaneViewModel> IdleLanes { get; }

    [ObservableProperty] private bool _hasRunning;

    /// <summary>Stopped and never finished — their own band, above the days and outside them (ET-179).</summary>
    public ObservableCollection<UnfinishedRunViewModel> UnfinishedRuns { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowUnfinishedBand))]
    private bool _hasUnfinishedRuns;

    public bool ShowUnfinishedBand => HasUnfinishedRuns && IsLocalTabSelected;

    /// <summary>Why the band has nobody in it, when it has not.</summary>
    public string? LanesEmptyText { get; }

    /// <summary>"Runs for 'Woensdag Homefronts'" when opened from a fleet's RUNS button (ET-185), null otherwise —
    /// so the header says what narrowed the list down, rather than the screen quietly showing fewer runs than the
    /// reader expects from the app's one runs screen.</summary>
    public string? FleetFilterText => _fleetFilter is { } filter ? $"Runs for '{filter.FleetName}'" : null;

    /// <summary>Why nothing is listed — a failed read and an empty history are different things and say so.</summary>
    [ObservableProperty] private string? _statusMessage;

    // ══ The pane, the selection and the drawer (ET-291) ═════════════════════════════════════════════════════════

    /// <summary>The selected run, read out in full. One view model for both hosts — the column beside the list and the
    /// drawer over it — of which one is ever on screen.</summary>
    public RunsActivityPaneViewModel Pane { get; }

    /// <summary>SUMMARY (ET-294): the day, week or month added up, in the same two hosts as <see cref="Pane"/>.</summary>
    public RunsSummaryViewModel Summary { get; }

    /// <summary>The reader asked for the summary rather than the selected run — SUMMARY on the range line. A click on a
    /// row or a TOP RUNS line asks for the run again.</summary>
    [ObservableProperty] private bool _isSummaryChosen;

    /// <summary>Wide: the pane beside the list holds the summary when it was asked for, and whenever there is no run
    /// to show — never an empty column (ET-294).</summary>
    public bool ShowsSummaryInPane => IsSummaryChosen || SelectedRow is null;

    /// <summary>What the drawer holds on the narrow layout. Kept while it slides shut, so it leaves with its content.</summary>
    public bool ShowsSummaryInDrawer => IsSummaryChosen;

    /// <summary>SUMMARY's <c>on</c> state: the summary is actually on screen.</summary>
    public bool IsSummaryOn => IsWide ? ShowsSummaryInPane : IsDrawerOpen && IsSummaryChosen;

    public string DrawerTitle => IsSummaryChosen ? "SUMMARY" : "ACTIVITY";

    /// <summary>Wide, it swaps the pane between the run and the summary — with no run selected there is nothing to swap
    /// to, and it stays on. Narrow, it opens the drawer on the summary.</summary>
    [RelayCommand]
    public void ToggleSummary()
    {
        if (IsWide)
        {
            IsSummaryChosen = !(ShowsSummaryInPane && SelectedRow is not null);
            return;
        }

        IsSummaryChosen = true;
        IsDrawerOpen = true;
    }

    // ══ PUBLISH n LOCAL on the range line (RO-6) ═══════════════════════════════════════════════════════════════

    /// <summary>Whether a batch publish — this day, or the whole view — is under way. Both buttons share one flag:
    /// they would otherwise queue the same run twice, and the confirmation for one would sit behind the other's.</summary>
    [ObservableProperty] private bool _isPublishingMany;

    partial void OnIsPublishingManyChanged(bool value) => _ApplyFiltersToTabs();

    /// <summary>Every local activity the selected tab shows after filters — what PUBLISH n LOCAL sends, never more
    /// than what filtering already narrowed the screen to (§RO-6 "in beeld is ná filters").</summary>
    public int LocalInViewCount => SelectedTab?.Days.SelectMany(day => day.Rows).Count(row => row.IsLocal) ?? 0;

    public bool ShowPublishView => LocalInViewCount > 0 && _canPublish;

    public string PublishViewButtonText =>
        IsPublishingMany ? "PUBLISHING…" : $"⤒ PUBLISH {LocalInViewCount} LOCAL";

    [RelayCommand]
    private async Task PublishViewAsync()
    {
        if (SelectedTab is { } tab)
            await _PublishManyAsync([.. tab.Days.SelectMany(day => day.Rows)]);
    }

    /// <summary>The day header's own "n local ↑" (RO-6).</summary>
    private async Task _PublishDayAsync(RunsDayViewModel day) => await _PublishManyAsync([.. day.Rows]);

    /// <summary>A TOP RUNS line: that run selected and shown — beside the list, or in the drawer — with its day unfolded
    /// and the row scrolled into view. A run from a week's other month brings that month into view first.</summary>
    public async Task OpenSummaryRunAsync(RunsSummaryRunLine line)
    {
        IsSummaryChosen = false;
        if (_RowOf(line.ActivitySummaryId) is null)
        {
            var month = new DateOnly(line.Day.Year, line.Day.Month, 1);
            if (month == _MonthInView)
                return;

            await _GoToMonthAsync(month, keepRange: true);
        }

        if (SelectedTab is not { } tab || _RowOf(line.ActivitySummaryId) is not { } row)
            return;

        tab.ExpandDays([line.Day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local)]);
        Select(row);
        RowScrollRequested?.Invoke(row);
    }

    private ActivityOverviewRowViewModel? _RowOf(Guid activitySummaryId) =>
        SelectedTab?.Days.SelectMany(day => day.Rows).FirstOrDefault(row => row.ActivitySummaryId == activitySummaryId);

    partial void OnIsSummaryChosenChanged(bool value) => _SyncSummaryShown();

    partial void OnIsDrawerOpenChanged(bool value) => _SyncSummaryShown();

    partial void OnSelectedRowChanged(ActivityOverviewRowViewModel? value)
    {
        _SyncSummaryShown();
        // The summary's day and week follow the selection.
        _ShowSummary();
    }

    private void _SyncSummaryShown()
    {
        OnPropertyChanged(nameof(ShowsSummaryInPane));
        OnPropertyChanged(nameof(ShowsSummaryInDrawer));
        OnPropertyChanged(nameof(IsSummaryOn));
        OnPropertyChanged(nameof(DrawerTitle));
        if (Summary is not null)
            Summary.IsActive = IsSummaryOn;
    }

    /// <summary>The summary's input, from what the range line was just drawn from — never a read.</summary>
    private void _ShowSummary()
    {
        if (Summary is null)
            return;

        RunsTabViewModel[] servers = [.. Tabs.Where(tab => !tab.IsLocal)];
        Summary.Show(new RunsSummaryInput(_tabDays, _Today, _MonthInView, RangeKind, RangeStart,
            SelectedRow is { } row ? DateOnly.FromDateTime(row.StartedAtLocal) : null, _FirstDay, Strip.Shade,
            servers.Length == 1 ? (servers[0].ServerAddress!, servers[0].Header) : null, _namesById));
    }

    partial void OnRangeKindChanged(RunsRangeKind value) => Summary?.ResetScope();

    partial void OnRangeStartChanged(DateOnly value) => Summary?.ResetScope();

    partial void OnViewedMonthLocalChanged(DateTime value) => Summary?.ResetScope();

    /// <summary>Whether there is room for the pane beside the list. Set from the bounds of the module content, so a
    /// docked tab and a floating window each answer for the width they were actually given.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PaneColumnWidth))]
    [NotifyPropertyChangedFor(nameof(IsNarrow))]
    private bool _isWide;

    public bool IsNarrow => !IsWide;

    /// <summary>An explicit column, never <c>Auto</c> (ET-285): 400 px beside the list, and nothing at all below the
    /// breakpoint, where the same pane is the drawer instead.</summary>
    public GridLength PaneColumnWidth => IsWide ? new GridLength(RunsLayout.PaneWidth) : new GridLength(0);

    /// <summary>The drawer is over the list, so it is only ever open on the narrow layout. Widening past the
    /// breakpoint closes it: the pane beside the list is already showing the very same run.</summary>
    [ObservableProperty] private bool _isDrawerOpen;

    /// <summary>The selected activity, whether or not the list can currently show it. This, not
    /// <c>ListBox.SelectedItem</c>, is what "selected" means on this screen.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private ActivityOverviewRowViewModel? _selectedRow;

    public bool HasSelection => SelectedRow is not null;

    /// <summary>What the list's own <c>SelectedItem</c> is bound to: the selected row while its day is unfolded, and
    /// null while it is not. A null <i>from</i> the list is ignored (see <see cref="OnListSelectionChanged"/>) — a
    /// folded day is not "out of view", and clearing the selection because the list lost sight of the row is exactly
    /// the bug ET-290's folding introduced.</summary>
    [ObservableProperty] private object? _listSelection;

    partial void OnListSelectionChanged(object? value)
    {
        if (value is ActivityOverviewRowViewModel row)
        {
            Select(row);
            return;
        }

        // A ListBox whose selected item leaves its source does not go empty: it takes the neighbour at the same
        // index, which after folding a day is that day's own header. Only an activity row is selectable on this
        // screen — a day header and a pilot's run are not — so anything else is handed straight back.
        if (value is not null)
            ListSelection = null;
    }

    partial void OnIsWideChanged(bool value)
    {
        if (value)
            IsDrawerOpen = false;
        _SyncSummaryShown();
    }

    partial void OnSelectedTabChanged(RunsTabViewModel? value)
    {
        _SettleSelection();
        // The constructor sets the first tab before the strip and the pane exist.
        if (Pane is not null)
        {
            _RefreshRange();
            // Every tab keeps its own filtered Days already (see _ApplyFiltersToTabs); only the tile counts and the
            // set of types this tab has to offer are tab-scoped and need to be drawn again.
            _RefreshFilterTiles();
        }
    }

    /// <summary>The width the module content was handed. One place decides what "wide" means (ET-291).</summary>
    public void ApplyWidth(double width) => IsWide = width >= RunsLayout.WideFrom;

    /// <summary>A click on a row, or on one of its pilots' runs. On the narrow layout it opens the drawer as well —
    /// the pane has nowhere else to be drawn there.</summary>
    /// <param name="showRun">A click asks for the run and takes the summary off; ↑↓ with the summary open leave it
    /// there, where its day and week follow the step.</param>
    public void Select(ActivityOverviewRowViewModel row, bool showRun = true)
    {
        if (showRun)
            IsSummaryChosen = false;
        bool moved = !ReferenceEquals(SelectedRow, row);
        if (moved)
        {
            SelectedRow = row;
            Pane.Show(row);
        }

        if (!IsWide)
            IsDrawerOpen = true;
        _SyncListSelection();
    }

    /// <summary>↑ and ↓ over the activity rows of unfolded days, in the order the list draws them: pilots' runs and
    /// day headers are stepped past, and a folded day is stepped over without being unfolded (Jithran, 15 Sep). With
    /// nothing selected yet, a step picks the first row there is.</summary>
    public void MoveSelection(int direction)
    {
        if (SelectedTab is not { } tab || direction == 0)
            return;

        // Every row of every day, folded or not — so a selection inside a folded day still has a place to step from.
        List<(RunsDayViewModel Day, ActivityOverviewRowViewModel Row)> rows =
            [.. tab.Days.SelectMany(day => day.Rows.Select(row => (Day: day, Row: row)))];
        if (rows.Count == 0)
            return;

        int from = SelectedRow is null ? -1 : rows.FindIndex(pair => ReferenceEquals(pair.Row, SelectedRow));
        int step = Math.Sign(direction);
        for (int index = from < 0 ? (step > 0 ? 0 : rows.Count - 1) : from + step;
             index >= 0 && index < rows.Count;
             index += step)
        {
            if (!rows[index].Day.IsExpanded)
                continue;

            Select(rows[index].Row, showRun: false);
            return;
        }
    }

    /// <summary>↵, a double-click on a row, or the pane's own OPEN DETAIL: the ET-162 screen for the selected
    /// activity. On the narrow layout with the drawer shut, ↵ opens the drawer instead — there is a step between the
    /// list and a whole screen there.</summary>
    public Task OpenSelectedDetailAsync()
    {
        if (SelectedRow is not { } row)
            return Task.CompletedTask;

        if (!IsWide && !IsDrawerOpen)
        {
            IsDrawerOpen = true;
            return Task.CompletedTask;
        }

        return _OpenDetailAsync(row);
    }

    /// <summary>✕, a click on the scrim, or Esc. The selection stays: the reader closed a panel, they did not
    /// unpick a run.</summary>
    [RelayCommand]
    public void CloseDrawer() => IsDrawerOpen = false;

    /// <summary>Whether the pane's PUBLISH can name where it is going. One coupled server is named; a choice between
    /// several is made in the dialog the button already opens, so the button says the plain verb.</summary>
    private string? _PublishTargetName()
    {
        RunsTabViewModel[] servers = [.. Tabs.Where(tab => !tab.IsLocal)];
        return servers.Length == 1 ? servers[0].Header : null;
    }

    /// <summary>The pane's own read (ET-291): the pilots' runs and the loot lines behind the selected activity. Off
    /// the UI thread — this is the same eight-to-ten round trips plus a market lookup the detail screen makes.</summary>
    private async Task<RunsPaneDetail> _ReadPaneDetailAsync(ActivityOverviewRowViewModel row, CancellationToken cancellationToken)
    {
        return await Task.Run<RunsPaneDetail>(async () =>
        {
            Result<ActivityDetailDto> detail = await _DetailOfAsync(row);
            if (!detail.IsSuccess || detail.Value is not { } activity)
                return new RunsPaneDetail([], 0, null,
                    detail.Messages.Count > 0 ? detail.Messages[0].Text : "The runs could not be read.");

            cancellationToken.ThrowIfCancellationRequested();
            // Resolved once for the activity, before a single row is built (ET-306): a crew member with no run-start
            // snapshot and no local character reads their real name from here, never a permanent "character {id}".
            await _characterNames.HydrateAsync(activity.Runs.Select(run => run.CharacterId), cancellationToken);
            ActivityRunRowViewModel[] crew = [.. activity.Runs.OrderBy(run => run.StartedAtUtc).Select(run =>
                new ActivityRunRowViewModel(run, _NameOf,
                    _FaceOf(run.CharacterId, CharacterNameResolver.Resolve(run.CharacterNameSnapshot, run.CharacterId, _NameOf)),
                    activity.IskByCharacter?.GetValueOrDefault(run.CharacterId)))];
            // The lines a pilot actually kept: a capture they excluded is not loot, and the activity's own LootIskNet
            // already leaves it out, so counting it here would put an item count beside a figure that never held it.
            int lootItems = activity.Runs
                .SelectMany(run => run.LootCaptures.Where(capture => !capture.IsExcluded))
                .Sum(capture => capture.Entries.Count);
            return new RunsPaneDetail(crew, lootItems, activity.LootIskNet, null);
        }, cancellationToken);
    }

    /// <summary>A server row carries its detail from the read that built it (ET-311): there is nothing of it in the
    /// local store to ask for. A local row reads ET-160's own query, as before.</summary>
    private Task<Result<ActivityDetailDto>> _DetailOfAsync(ActivityOverviewRowViewModel row) =>
        row.ServerDetail is { } detail
            ? Task.FromResult(Result<ActivityDetailDto>.Success(detail))
            : _dispatcher.Query(new GetActivityDetailQuery(row.ActivitySummaryId));

    /// <summary>Brings the selection back in line with what the tab now holds — after a read, a tab switch, or a day
    /// being folded or unfolded. An activity that is gone from the tab altogether takes the selection and the drawer
    /// with it; one that is merely inside a folded day keeps both.</summary>
    private void _SettleSelection()
    {
        if (SelectedTab is not { } tab || SelectedRow is not { } selected)
        {
            _SyncListSelection();
            return;
        }

        // A refresh may have replaced the instance with an equal one (ET-222), which is still the same activity.
        ActivityOverviewRowViewModel? still = tab.Days
            .SelectMany(day => day.Rows)
            .FirstOrDefault(row => row.ActivitySummaryId == selected.ActivitySummaryId);
        if (still is null)
        {
            SelectedRow = null;
            // A drawer holding the summary has nothing of the run in it to lose.
            if (!IsSummaryChosen)
                IsDrawerOpen = false;
            Pane.Show(null);
            _SyncListSelection();
            return;
        }

        if (!ReferenceEquals(still, selected))
        {
            SelectedRow = still;
            Pane.Show(still);
        }

        _SyncListSelection();
    }

    private void _SyncListSelection() =>
        ListSelection = SelectedRow is { } row && SelectedTab?.Items.Contains(row) == true ? row : null;

    /// <summary>Every rebuild of a tab's flat sequence is a day folding or unfolding, or a read landing: whether the
    /// list can show the selected row may have changed with it. Once the rebuild is whole, not per change inside it —
    /// a <c>ListBox</c> half way through a reconcile is still moving its own <c>SelectedItem</c> onto whatever sits
    /// at the index the selected row left, and the last word has to be this screen's.</summary>
    private void _WatchItems(RunsTabViewModel tab) =>
        tab.ItemsRebuilt += _ => _SyncListSelection();

    public void RefreshModule() => _ = LoadAsync();

    /// <summary>Everything on the screen read again, after the day-old stopped runs are saved as they stand — this
    /// screen is where such a run would otherwise sit and be offered as unfinished long after it stopped being that
    /// (ET-179).</summary>
    public Task LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _ReadAsync(null, withAutoSave: true);
    }

    /// <summary>What a run can change on this screen, read again in place (ET-222) — without the auto-save
    /// <see cref="LoadAsync"/> runs first: a refresh answers a change and never makes one. Handed over by
    /// <see cref="RunChangeFeed"/> on the UI thread.</summary>
    private Task _RefreshAsync(RunChangeBatch changed) => _ReadAsync(changed, withAutoSave: false);

    /// <param name="changed">What moved, when this answers a change; null reads every open row's runs again.</param>
    private Task _ReadAsync(RunChangeBatch? changed, bool withAutoSave)
    {
        // A null batch is "everything" and swallows whatever it meets.
        if (!_isReadOwed)
            _owedChanges = changed;
        else if (_owedChanges is not null)
            _owedChanges = changed is null ? null : _Merged(_owedChanges, changed);
        _owedAutoSave |= withAutoSave;
        _isReadOwed = true;
        if (_reading is { } inFlight)
            return inFlight.Task;

        var reading = _reading = new TaskCompletionSource();
        _ = _ReadWhileOwedAsync(reading);
        return reading.Task;
    }

    private async Task _ReadWhileOwedAsync(TaskCompletionSource reading)
    {
        try
        {
            while (_isReadOwed)
            {
                RunChangeBatch? changed = _owedChanges;
                bool withAutoSave = _owedAutoSave;
                _isReadOwed = false;
                _owedChanges = null;
                _owedAutoSave = false;
                await _ReadOnceAsync(changed, withAutoSave);
            }

            _reading = null;
            reading.SetResult();
        }
        catch (Exception exception)
        {
            _isReadOwed = false;
            _owedChanges = null;
            _owedAutoSave = false;
            _reading = null;
            reading.SetException(exception);
        }
    }

    private static RunChangeBatch _Merged(RunChangeBatch first, RunChangeBatch second)
    {
        var merged = new RunChangeBatch();
        merged.Add(first);
        merged.Add(second);
        return merged;
    }

    /// <summary>One pass: everything read and every row built off the UI thread, then shown here.</summary>
    private async Task _ReadOnceAsync(RunChangeBatch? changed, bool withAutoSave)
    {
        (DateOnly readFrom, DateOnly readTo) = _ReadRange();
        Dictionary<string, IReadOnlyDictionary<Guid, ActivityOverviewRowViewModel>> shownServerRows = [];
        foreach (RunsTabViewModel tab in Tabs)
            if (tab.ServerAddress is { } address)
                shownServerRows[address] = tab.Days.SelectMany(day => day.Rows).ToDictionary(row => row.ActivitySummaryId);
        var request = new ScreenReadRequest(ViewedMonthLocal, readFrom, readTo,
            LocalTab.Days.SelectMany(day => day.Rows).ToDictionary(row => row.ActivitySummaryId), shownServerRows,
            Tabs.Where(tab => tab.ServerAddress is not null).ToDictionary(tab => tab.ServerAddress!, tab => tab.Header),
            withAutoSave, changed);
        ScreenRead read = await Task.Run(() => _ReadScreenAsync(request));

        _ShowRunning(read.Running);
        _ShowUnfinished(read.Unfinished);
        foreach ((string address, string header) in read.NewServers)
        {
            if (Tabs.Any(tab => tab.ServerAddress == address))
                continue;

            var added = new RunsTabViewModel(header, address, _PublishDayAsync);
            _WatchItems(added);
            Tabs.Add(added);
        }

        HasServerTabs = Tabs.Count > 1;
        _canPublish = read.CanPublish;

        if (read.OverviewSkipped)
            return;

        if (read.Rows is null)
        {
            // What is on screen stays: a read that failed says nothing about what the days hold.
            StatusMessage = read.OverviewError;
            return;
        }

        StatusMessage = null;
        _fleetHistoryKnownEmpty = read.FleetHistoryKnownEmpty;
        List<Task> subRunReads = [];
        _AdoptRows(read.Rows, changed, subRunReads);
        _loadedRows = [.. read.Rows.Select(pair => pair.Row)];
        if (read.ServerReads is { } serverReads)
        {
            foreach (ServerTabRead serverRead in serverReads.Values)
                _AdoptRows(serverRead.Rows, changed, subRunReads);
            _serverReads = serverReads;
        }
        _ApplyFiltersToTabs();

        _loadedFacts = read.Facts;
        _loadedFrom = request.ReadFrom;
        _loadedTo = request.ReadTo;
        _firstTracked = read.FirstTracked;
        _RefreshFilterTiles();

        // The selected activity may have been replaced by an equal instance, or have left this month altogether.
        ActivityOverviewRowViewModel? selectedBefore = SelectedRow;
        _SettleSelection();
        // A live refresh keeps a picked day or week, and the days it unfolded stay unfolded (Days keep their fold).
        _RefreshRange();
        // A change that reached the activity on show, without its own row having moved: the pane's crew and loot are
        // read again, the head it is already drawing left alone.
        if (SelectedRow is { } stillSelected && ReferenceEquals(stillSelected, selectedBefore)
            && (changed is null || changed.Concerns(stillSelected.RunId is { } selectedRunId ? [selectedRunId] : [], stillSelected.GroupCode)))
            Pane.RereadDetail();

        await Task.WhenAll(subRunReads);
    }

    /// <summary>The rows one read built, taken onto the screen: a new row is wired up and takes over from the one it
    /// replaces; an unchanged row that is open has its runs read again when the change reached them, since a pilot's
    /// share or a corrected time moves a run without moving the activity's figures.</summary>
    private void _AdoptRows(IReadOnlyList<(ActivityOverviewRowViewModel Row, ActivityOverviewRowViewModel? Previous)> rows,
        RunChangeBatch? changed, List<Task> subRunReads)
    {
        foreach ((ActivityOverviewRowViewModel row, ActivityOverviewRowViewModel? previous) in rows)
        {
            if (ReferenceEquals(row, previous))
            {
                if (row.IsExpanded && (changed is null || changed.Concerns(row.RunId is { } runId ? [runId] : [], row.GroupCode)))
                    subRunReads.Add(_LoadSubRunsAsync(row));
                continue;
            }

            row.LayoutChanged += _OnRowLayoutChanged;
            row.SelectRequested += selected => Select(selected);
            if (previous is not null)
                subRunReads.Add(row.ContinueFromAsync(previous));
        }
    }

    /// <summary>Off the UI thread: every read one pass needs, and the rows built from them. A row already on screen
    /// that still says the same is handed back as itself (ET-222); anything else is built new here, its type and
    /// system answered by <see cref="_facts"/>.</summary>
    private async Task<ScreenRead> _ReadScreenAsync(ScreenReadRequest request)
    {
        // Servers first: whether a row offers PUBLISH is part of what it shows.
        (bool canPublish, IReadOnlyList<(string Address, string Header)> newServers, IReadOnlyDictionary<string, string> headers) =
            await _ReadServersAsync(request.ServerHeaders);

        Result<IReadOnlyList<RunningRunDto>> running = await _dispatcher.Query(new GetRunningRunsQuery());
        RunningRunFacts[] runningFacts = [.. (running.IsSuccess ? running.Value ?? [] : []).Select(run => new RunningRunFacts(run,
            _facts.TypeOf(run.ActivityKind, run.SignatureGroupSnapshot, run.SiteTypeId, run.SiteName).Name,
            _facts.SystemOf(run.SolarSystemId)?.Name))];

        if (request.WithAutoSave)
            await _dispatcher.Send(new SaveRunsLeftUnfinishedCommand(DateTime.UtcNow));
        Result<IReadOnlyList<UnfinishedRunDto>> unfinished = await _dispatcher.Query(new GetUnfinishedRunsQuery());

        // A bounty or a loot line landing on a run that is still running changes nothing in the list or the strip: a
        // running run has no activity yet. Only RUNNING and UNFINISHED are read for such a batch — a run being saved has
        // left GetRunningRunsQuery by the time its batch arrives, so a save still reads everything (ET-292).
        if (request.Changed is { IsUnscoped: false } changed && changed.RunIds.Count > 0 && running.IsSuccess
            && changed.RunIds.All(runId => runningFacts.Any(run => run.Run.Id == runId))
            && changed.GroupCodes.All(groupCode => runningFacts.Any(run => run.Run.GroupCode == groupCode)))
            return new ScreenRead(runningFacts, unfinished.Value ?? [], newServers, canPublish, null, null, false,
                [], null, OverviewSkipped: true);

        // One read over the strip and the month in view together; rows are built for the month only, and the rest is
        // counted as facts for the strip and the range line (ET-292).
        DateTime fromUtc = request.ReadFrom.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local).ToUniversalTime();
        DateTime toUtc = request.ReadTo.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local).ToUniversalTime();
        (DateTime monthFromUtc, DateTime monthToUtc) = _MonthRangeUtc(request.MonthLocal);
        // This machine's own characters, so every row comes back with their share of it rather than the group's
        // (ET-296) — worked out in the handler, where the stored split is and where the read already runs off the UI
        // thread. Not a filter: a fleet mate's run pulled in by sync keeps its row, it just earns nothing here.
        Result<IReadOnlyList<ActivityOverviewRowDto>> overview = await _dispatcher.Query(
            new GetActivityOverviewQuery(fromUtc, toUtc, FleetId: _fleetFilter?.FleetId,
                OwnCharacterIds: [.. _namesById.Keys]));
        if (!overview.IsSuccess || overview.Value is null)
            return new ScreenRead(runningFacts, unfinished.Value ?? [], newServers, canPublish, null,
                overview.Messages.Count > 0 ? overview.Messages[0].Text : "The activities could not be read.", false,
                [], null, OverviewSkipped: false);

        Result<DateTime?> firstStart = await _dispatcher.Query(new GetFirstActivityStartQuery());
        DateOnly? firstTracked = firstStart is { IsSuccess: true, Value: { } firstUtc }
            ? DateOnly.FromDateTime(firstUtc.ToLocalTime())
            : null;
        RunsActivityFacts[] facts = [.. overview.Value.Select(dto => RunsActivityFacts.From(dto, _facts))];
        ActivityOverviewRowDto[] monthRows = [.. overview.Value
            .Where(dto => dto.StartedAtUtc >= monthFromUtc && dto.StartedAtUtc < monthToUtc)];

        Func<string, string> serverNameOf = address => headers.GetValueOrDefault(address) ?? address;
        List<(ActivityOverviewRowViewModel Row, ActivityOverviewRowViewModel? Previous)> rows =
            await _BuildRowsAsync(monthRows, request.ShownRows, canPublish, serverNameOf, null);
        // The server tabs read their servers on a full read only (ET-311): a live change is a change to the local
        // store, and says nothing about what a server holds.
        IReadOnlyDictionary<string, ServerTabRead>? serverReads = request.Changed is null
            ? await _ReadServerTabsAsync(request, headers, fromUtc, toUtc, monthFromUtc, monthToUtc, serverNameOf)
            : null;

        // Only worth asking when the fleet filter came up empty — a coverage query a full result already answers by
        // existing (ET-185: GetFleetRunCoverageQuery is the "why is this empty" question, not the "what is here" one).
        bool fleetHistoryKnownEmpty = monthRows.Length > 0 || _fleetFilter is null
            || await _IsFleetHistoryKnownEmptyAsync(_fleetFilter);
        return new ScreenRead(runningFacts, unfinished.Value ?? [], newServers, canPublish, rows, null, fleetHistoryKnownEmpty,
            facts, firstTracked, OverviewSkipped: false, serverReads);
    }

    /// <summary>Rows for the month's activities, built off the UI thread. A row already on screen that still says the
    /// same is handed back as itself (ET-222). Every crew member and other earner is resolved once here rather than one
    /// row at a time turning up its own fallback the moment it is built (ET-306).</summary>
    /// <param name="serverDetails">A server tab's detail per activity, carried on its rows; null on Local, whose rows
    /// read the store.</param>
    private async Task<List<(ActivityOverviewRowViewModel Row, ActivityOverviewRowViewModel? Previous)>> _BuildRowsAsync(
        IReadOnlyList<ActivityOverviewRowDto> monthRows, IReadOnlyDictionary<Guid, ActivityOverviewRowViewModel> shownRows,
        bool canPublish, Func<string, string> serverNameOf, IReadOnlyDictionary<Guid, ActivityDetailDto>? serverDetails)
    {
        await _characterNames.HydrateAsync(monthRows
            .SelectMany(dto => dto.Crew.Select(member => member.CharacterId)
                .Concat(dto.OtherEarners.Select(member => member.CharacterId))));

        List<(ActivityOverviewRowViewModel Row, ActivityOverviewRowViewModel? Previous)> rows = [];
        foreach (ActivityOverviewRowDto dto in monthRows)
        {
            shownRows.TryGetValue(dto.ActivitySummaryId, out ActivityOverviewRowViewModel? shown);
            ActivityDetailDto? serverDetail = serverDetails?.GetValueOrDefault(dto.ActivitySummaryId);
            // A server row is published by definition: nothing to publish from it, no automatic publish to report on it.
            bool publishable = canPublish && serverDetail is null;
            RunPublishProgress? progress = serverDetail is null ? _autoPublisher?.ProgressFor(dto.GroupCode) : null;
            // A server row is never kept: its detail travels on it and is not part of what IsShowing compares, and a
            // server read is a full read only, so ET-287's per-tick cost does not apply; fold and selection carry over.
            ActivityOverviewRowViewModel row = shown is not null && serverDetails is null && shown.IsShowing(dto, publishable, progress)
                ? shown
                : new ActivityOverviewRowViewModel(dto, _NameOf, _LoadSubRunsAsync, _OpenDetailAsync,
                    publishable ? _PublishAsync : null, serverNameOf, progress, _RetryPublishAsync, _facts, _FaceOf, serverDetail);
            rows.Add((row, shown));
        }

        return rows;
    }

    /// <summary>Each server tab's own read (ET-311), over the window Local was read for, through the characters
    /// connected to it: a server none of them is connected to is not asked and says so on its tab, a read that fails
    /// says why there, and Local is untouched either way. Rows for the month, facts for the strip, like Local's.</summary>
    private async Task<IReadOnlyDictionary<string, ServerTabRead>> _ReadServerTabsAsync(ScreenReadRequest request,
        IReadOnlyDictionary<string, string> headers, DateTime fromUtc, DateTime toUtc, DateTime monthFromUtc, DateTime monthToUtc,
        Func<string, string> serverNameOf)
    {
        Dictionary<string, ServerTabRead> reads = [];
        IRemoteBusConnector? connector = _services.GetService<IRemoteBusConnector>();
        foreach ((string address, string header) in headers)
        {
            long[] connected = [.. _namesById.Keys
                .Where(characterId => connector?.StateFor(address, (int)characterId) == ServerConnectionState.Connected)];
            if (connected.Length == 0)
            {
                reads[address] = new ServerTabRead([], [], $"Not connected to {header}.");
                continue;
            }

            Result<IReadOnlyList<ServerActivityDto>> read;
            using (IServiceScope scope = _services.CreateScope())
                read = await scope.ServiceProvider.GetRequiredService<ServerRunsReader>()
                    .ReadAsync(address, connected, fromUtc, toUtc, _fleetFilter?.FleetId, [.. _namesById.Keys]);
            if (!read.IsSuccess || read.Value is not { } activities)
            {
                reads[address] = new ServerTabRead([], [],
                    read.Messages.Count > 0 ? read.Messages[0].Text : $"{header} could not be read.");
                continue;
            }

            ActivityOverviewRowDto[] monthRows = [.. activities.Select(activity => activity.Row)
                .Where(dto => dto.StartedAtUtc >= monthFromUtc && dto.StartedAtUtc < monthToUtc)];
            reads[address] = new ServerTabRead(
                await _BuildRowsAsync(monthRows,
                    request.ShownServerRows.GetValueOrDefault(address) ?? new Dictionary<Guid, ActivityOverviewRowViewModel>(),
                    canPublish: false, serverNameOf,
                    activities.ToDictionary(activity => activity.Row.ActivitySummaryId, activity => activity.Detail)),
                [.. activities.Select(activity => RunsActivityFacts.From(activity.Row, _facts))], null);
        }

        return reads;
    }

    /// <summary>Every coupled server, and a name for each one this screen has no tab for yet — never rebuilding the
    /// strip, since the reader's chosen tab must survive a refresh. Which is also why a decoupled server keeps its tab
    /// until the screen is reopened: the activities on it are still true.</summary>
    private async Task<(bool CanPublish, IReadOnlyList<(string Address, string Header)> NewServers, IReadOnlyDictionary<string, string> Headers)>
        _ReadServersAsync(IReadOnlyDictionary<string, string> knownHeaders)
    {
        IClientSessionStore? sessionStore = _services.GetService<IClientSessionStore>();
        if (sessionStore is null)
            return (false, [], knownHeaders);

        IServerRegistry? registry = _services.GetService<IServerRegistry>();
        IReadOnlyList<string> servers = await sessionStore.ListServersAsync();
        Dictionary<string, string> headers = new(knownHeaders);
        List<(string Address, string Header)> added = [];
        foreach (string address in servers)
        {
            if (headers.ContainsKey(address))
                continue;

            string header = registry is null ? address : await registry.DisplayNameAsync(address);
            headers[address] = header;
            added.Add((address, header));
        }

        return (servers.Count > 0, added, headers);
    }

    private void _ShowRunning(IReadOnlyList<RunningRunFacts> running)
    {
        DateTime nowUtc = DateTime.UtcNow;
        foreach (RunningLaneViewModel lane in Lanes)
        {
            RunningRunFacts? facts = running.FirstOrDefault(run => (long?)lane.Character.EsiCharacterId == run.Run.CharacterId);
            lane.Attach(facts?.Run, nowUtc, facts?.TypeText ?? string.Empty, facts?.SystemText);
        }

        // Only groups one of this machine's own characters is on: a running row left behind for a character that is
        // not local shows nothing (ET-203).
        Dictionary<string, RunningGroupViewModel> shownGroups = RunningGroups.ToDictionary(group => group.Key);
        List<RunningGroupViewModel> groups = [];
        foreach (IGrouping<string, RunningLaneViewModel> group in Lanes
                     .Where(lane => lane.Run is not null)
                     .GroupBy(lane => lane.Run!.GroupCode ?? lane.Run.Id.ToString())
                     .OrderBy(group => group.Min(lane => lane.Run!.StartedAtUtc)))
        {
            RunningGroupViewModel line = shownGroups.GetValueOrDefault(group.Key)
                ?? new RunningGroupViewModel(group.Key, _OpenRunningGroupAsync);
            line.Show([.. group], nowUtc);
            groups.Add(line);
        }

        RunningGroups.ReconcileTo(groups);
        for (int index = 0; index < groups.Count; index++)
            groups[index].IsFirst = index == 0;
        IdleLanes.ReconcileTo([.. Lanes.Where(lane => !lane.IsRunning)]);
        HasRunning = groups.Count > 0;
    }

    private void _ShowUnfinished(IReadOnlyList<UnfinishedRunDto> unfinished)
    {
        Dictionary<Guid, UnfinishedRunViewModel> shown = UnfinishedRuns.ToDictionary(run => run.RunId);
        UnfinishedRuns.ReconcileTo([.. unfinished.Select(run =>
            shown.TryGetValue(run.RunId, out UnfinishedRunViewModel? same) && same.IsShowing(run)
                ? same
                : new UnfinishedRunViewModel(run, _NameOf(run.CharacterId), _SaveUnfinishedRunAsync,
                    _DeleteUnfinishedRunAsync, _ResumeUnfinishedRunAsync))]);
        HasUnfinishedRuns = UnfinishedRuns.Count > 0;
    }

    /// <summary>A row unfolded, folded, or its runs came in: the lists that hold it follow.</summary>
    private void _OnRowLayoutChanged(ActivityOverviewRowViewModel row)
    {
        foreach (RunsTabViewModel tab in Tabs)
            tab.RebuildItems();
    }

    /// <summary>Why a tab shows nothing. Unfiltered, that is just "nothing saved" or "nothing published" — but a
    /// fleet-filtered screen (ET-185) has a third, more important answer to give: whether this fleet is confirmed to
    /// have flown nothing since ET-182 started recording it, or whether it is simply too old for that record to say
    /// either way. Reading the second case as the first is exactly the false zero this ticket exists to rule out.</summary>
    private string _EmptyMessageFor(RunsTabViewModel tab)
    {
        if (tab.ServerAddress is { } address && _serverReads.GetValueOrDefault(address)?.Error is { } error)
            return error;

        if (_fleetFilter is null)
            return tab.IsLocal
                ? "No activity has been saved yet. A run shows up here the moment you save it."
                : "Nothing published to this server yet. Publish an activity from Local to put it here.";

        return _fleetHistoryKnownEmpty
            ? $"'{_fleetFilter.FleetName}' has no completed runs on record."
            : $"'{_fleetFilter.FleetName}' may have flown runs before this client tracked which fleet a run belongs "
              + "to — those can't be shown here, only what it has flown since.";
    }

    /// <summary>Whether an empty fleet filter is a real zero: true when the fleet is confirmed to predate nothing
    /// (see <see cref="GetFleetRunCoverageQuery"/>), false when its age makes that unknowable.</summary>
    private async Task<bool> _IsFleetHistoryKnownEmptyAsync(RunsFleetFilter filter)
    {
        Result<FleetRunCoverageDto> coverage = await _dispatcher.Query(
            new GetFleetRunCoverageQuery(filter.FleetId, filter.FleetCreatedAtUtc));
        return coverage.IsSuccess && coverage.Value is { IsKnown: true };
    }

    /// <summary>Commit the run as it stands. Nothing is handed along: what the run window watched died with it, and
    /// the loot captures and bounty lines it wrote as they came in are already on the row.</summary>
    private async Task _SaveUnfinishedRunAsync(UnfinishedRunViewModel run)
    {
        DateTime nowUtc = DateTime.UtcNow;
        Guid runId = run.RunId;
        DateTime stoppedAtUtc = run.StoppedAtUtc ?? nowUtc;
        Result saved = await Task.Run(() => _dispatcher.Send(new SaveRunCommand(runId, stoppedAtUtc, nowUtc, [], [], [], [])));
        await _AfterFinishingAsync(saved, "The run could not be saved.");
    }

    private async Task _DeleteUnfinishedRunAsync(UnfinishedRunViewModel run)
    {
        if (!await _dialogs.ConfirmAsync("Throw this run away?",
                $"{run.SiteText} on {run.CharacterText} goes, with the loot and bounty recorded on it. "
                + "Saving keeps it instead.", "Delete"))
            return;

        Guid runId = run.RunId;
        Result deleted = await Task.Run(() => _dispatcher.Send(new DeleteRunCommand(runId, DateTime.UtcNow)));
        await _AfterFinishingAsync(deleted, "The run could not be thrown away.");
    }

    /// <summary>
    /// Reopens the run window on exactly this row and picks the clock back up (ET-254) — the same pause STOP/START
    /// already is inside an open window, just pressed by a caller other than the pilot's own click. Confirmed first,
    /// every time, with the honest stop time on the question: this row exists at all only because the app itself
    /// stopped it (a crash, or the previous process quitting with it still going), and nobody pressed STOP meaning
    /// to step away for good — but nobody should have yesterday's run dragged forward by a click either, so the
    /// question says when it actually stopped rather than assuming either answer.
    /// </summary>
    private async Task _ResumeUnfinishedRunAsync(UnfinishedRunViewModel run)
    {
        string stoppedText = run.StoppedAtUtc is { } stoppedAtUtc
            ? $"stopped {stoppedAtUtc.ToLocalTime():d MMM HH:mm}"
            : "never recorded a stop";
        if (!await _dialogs.ConfirmAsync("Resume this run?",
                $"{run.SiteText} on {run.CharacterText} — {stoppedText}. Its clock continues from the original "
                + "start, with everything it already collected.", "Resume"))
            return;

        ActivityWindowViewModel window = new(run.ActivityKind, _services);
        // Named before the window loads (ET-221, the same reason ManualRunStartViewModel names its own pilot): this
        // row's character is known outright, and asking again would be a question this command already answered.
        window.UseCharacter(checked((int)run.CharacterId), run.CharacterText);
        window.ResumeRun(run.RunId);
        _dialogs.ShowActivityWindow(window);
        // No refresh here: SetRunStoppedCommand (inside the window's own resume) publishes RunsChangedEvent, which
        // RunChangeFeed already relays to this screen (ET-222) — the row drops out of UNFINISHED on its own the
        // moment the resume actually lands, same as every other run-changing command this screen does not poll for.
    }

    /// <summary>The whole screen is read again rather than the row taken off the list: saving moves a run into the
    /// days below, so a list that only dropped its own row would show the run nowhere at all (ET-179 AC-2).</summary>
    private async Task _AfterFinishingAsync(Result outcome, string fallbackMessage)
    {
        if (!outcome.IsSuccess)
        {
            StatusMessage = outcome.Messages.Count > 0 ? outcome.Messages[0].Text : fallbackMessage;
            return;
        }

        await LoadAsync();
    }

    /// <summary>Publish one activity: pick the target as the fit browser does (one coupled server goes without
    /// asking), say what travels, then queue and synchronise. Only runs of characters coupled to that server are
    /// queued — the server refuses a run pushed by anyone but its owner, so a crewmate's run would sit Pending for a
    /// push that can never be accepted.</summary>
    private async Task _PublishAsync(ActivityOverviewRowViewModel row)
    {
        IClientSessionStore? sessionStore = _services.GetService<IClientSessionStore>();
        if (sessionStore is null)
            return;

        IReadOnlyList<string> servers = await Task.Run(() => sessionStore.ListServersAsync());
        if (servers.Count == 0)
        {
            _ReportPublish("Not coupled to any server — couple a character first.", ToastKind.Warning);
            return;
        }

        IServerRegistry? registry = _services.GetService<IServerRegistry>();
        string? targetAddress = servers.Count == 1
            ? servers[0]
            : await _SelectServerAsync(servers, registry, $"Publish '{row.SiteText}' to which server?");
        if (targetAddress is null)
        {
            _ReportPublish("Publish cancelled.", ToastKind.Information);
            return;
        }

        if (_services.GetService<IRemoteBusConnector>()?.StateFor(targetAddress) != ServerConnectionState.Connected)
        {
            _ReportPublish("Not connected to that server.", ToastKind.Warning);
            return;
        }

        Guid activityId = row.ActivitySummaryId;
        Result<ActivityDetailDto> detail = await Task.Run(() => _dispatcher.Query(new GetActivityDetailQuery(activityId)));
        if (!detail.IsSuccess || detail.Value is null)
        {
            _ReportPublish(detail.Messages.Count > 0 ? detail.Messages[0].Text : "The activity could not be read.", ToastKind.Error);
            return;
        }

        IReadOnlyList<ClientSessionTokens> coupled = await Task.Run(() => sessionStore.LoadAllAsync(targetAddress));
        List<ActivityRunDetailDto> ownRuns = [.. detail.Value.Runs
            .Where(run => coupled.Any(session => session.CharacterId == run.CharacterId))];
        if (ownRuns.Count == 0)
        {
            _ReportPublish("No run in this activity belongs to a character coupled to that server.", ToastKind.Warning);
            return;
        }

        string serverName = registry is null ? targetAddress : await registry.DisplayNameAsync(targetAddress);
        if (!await _dialogs.ConfirmAsync($"Publish to {serverName}?", _WhatTravels(ownRuns.Count, serverName), "Publish"))
        {
            _ReportPublish("Publish cancelled.", ToastKind.Information);
            return;
        }

        Result? refused = await Task.Run(async () =>
        {
            foreach (ActivityRunDetailDto run in ownRuns)
            {
                Result queued = await _dispatcher.Send(new QueueRunForServerSyncCommand(run.RunId, targetAddress));
                if (!queued.IsSuccess)
                    return queued;
            }

            return (Result?)null;
        });
        if (refused is not null)
        {
            _ReportPublish(refused.Messages.Count > 0 ? refused.Messages[0].Text : "The run could not be queued.", ToastKind.Error);
            return;
        }

        (bool accepted, string message) =
            await Task.Run(() => _SynchronizeAsync(targetAddress, [.. ownRuns.Select(run => run.CharacterId)]));

        // Read back first and report second: the runs changed either way — queued, or queued and accepted — and a
        // reload after the report would clear the status line that carries the outcome.
        await LoadAsync();
        if (accepted)
            _ReportPublish($"Published to {serverName}.", ToastKind.Success, "Activity published");
        else
            // The runs stay Pending on purpose: they are still meant for this server, so the next publish retries
            // them rather than the pilot having to notice they never arrived.
            _ReportPublish($"Publish rejected: {message}", ToastKind.Error, "Publish rejected");
    }

    /// <summary>Publish every local activity among <paramref name="rows"/> in one go (RO-6): the day header's "n
    /// local" and the range line's PUBLISH n LOCAL both funnel through here — the same confirmation, queueing and
    /// per-character synchronise <see cref="_PublishAsync"/> does for one activity, batched so a day of 28 is one
    /// dialog and one sync per character, never 28.</summary>
    private async Task _PublishManyAsync(IReadOnlyList<ActivityOverviewRowViewModel> rows)
    {
        if (IsPublishingMany)
            return;

        List<ActivityOverviewRowViewModel> localRows = [.. rows.Where(row => row.IsLocal)];
        if (localRows.Count == 0)
            return;

        IClientSessionStore? sessionStore = _services.GetService<IClientSessionStore>();
        if (sessionStore is null)
            return;

        IReadOnlyList<string> servers = await Task.Run(() => sessionStore.ListServersAsync());
        if (servers.Count == 0)
        {
            _ReportPublish("Not coupled to any server — couple a character first.", ToastKind.Warning);
            return;
        }

        IServerRegistry? registry = _services.GetService<IServerRegistry>();
        string? targetAddress = servers.Count == 1
            ? servers[0]
            : await _SelectServerAsync(servers, registry, $"Publish {localRows.Count} activities to which server?");
        if (targetAddress is null)
        {
            _ReportPublish("Publish cancelled.", ToastKind.Information);
            return;
        }

        if (_services.GetService<IRemoteBusConnector>()?.StateFor(targetAddress) != ServerConnectionState.Connected)
        {
            _ReportPublish("Not connected to that server.", ToastKind.Warning);
            return;
        }

        IsPublishingMany = true;
        try
        {
            Guid[] summaryIds = [.. localRows.Select(row => row.ActivitySummaryId)];
            Result<IReadOnlyList<ActivityRunForPublishDto>> read =
                await Task.Run(() => _dispatcher.Query(new GetActivityRunsForPublishQuery(summaryIds)));
            if (!read.IsSuccess || read.Value is null)
            {
                _ReportPublish(read.Messages.Count > 0 ? read.Messages[0].Text : "The activities could not be read.", ToastKind.Error);
                return;
            }

            IReadOnlyList<ClientSessionTokens> coupled = await Task.Run(() => sessionStore.LoadAllAsync(targetAddress));
            List<ActivityRunForPublishDto> ownRuns = [.. read.Value
                .Where(run => coupled.Any(session => session.CharacterId == run.CharacterId))];
            if (ownRuns.Count == 0)
            {
                _ReportPublish("No run in these activities belongs to a character coupled to that server.", ToastKind.Warning);
                return;
            }

            int publishedActivities = ownRuns.Select(run => run.ActivitySummaryId).Distinct().Count();
            int skippedActivities = summaryIds.Length - publishedActivities;

            string serverName = registry is null ? targetAddress : await registry.DisplayNameAsync(targetAddress);
            if (!await _dialogs.ConfirmAsync($"Publish {summaryIds.Length} activities to {serverName}?",
                    _WhatTravels(ownRuns.Count, serverName), "Publish"))
            {
                _ReportPublish("Publish cancelled.", ToastKind.Information);
                return;
            }

            Result? refused = await Task.Run(async () =>
            {
                foreach (ActivityRunForPublishDto run in ownRuns)
                {
                    Result queued = await _dispatcher.Send(new QueueRunForServerSyncCommand(run.RunId, targetAddress));
                    if (!queued.IsSuccess)
                        return queued;
                }

                return (Result?)null;
            });
            if (refused is not null)
            {
                _ReportPublish(refused.Messages.Count > 0 ? refused.Messages[0].Text : "A run could not be queued.", ToastKind.Error);
                return;
            }

            (bool accepted, string message) = await Task.Run(
                () => _SynchronizeAsync(targetAddress, [.. ownRuns.Select(run => run.CharacterId).Distinct()]));

            // Read back first and report second, same as the single-activity publish above: a reload after the
            // report would clear the status line that carries the outcome.
            await LoadAsync();
            if (accepted)
            {
                string outcome = skippedActivities > 0
                    ? $"Published {publishedActivities} of {summaryIds.Length} to {serverName} · {skippedActivities} "
                      + $"had no run of a character coupled to {serverName}"
                    : $"Published {publishedActivities} to {serverName}.";
                _ReportPublish(outcome, ToastKind.Success, "Activities published");
            }
            else
            {
                // Same as the single-activity publish: the runs stay Pending, meant for the next attempt.
                _ReportPublish($"Publish rejected: {message}", ToastKind.Error, "Publish rejected");
            }
        }
        finally
        {
            IsPublishingMany = false;
        }
    }

    /// <summary>One synchronisation per owning character: the server attributes a push to the session it came in on,
    /// so two of this machine's pilots in the same activity — or the same day — are two pushes, not one. Stops at
    /// the first refusal — what the server said about it is worth more than a second attempt's message.</summary>
    private async Task<(bool Accepted, string Message)> _SynchronizeAsync(string targetAddress, IReadOnlyList<long> characterIds)
    {
        using IServiceScope scope = _services.CreateScope();
        RunSynchronizationService synchronization = scope.ServiceProvider.GetRequiredService<RunSynchronizationService>();
        foreach (long characterId in characterIds.Distinct())
        {
            (bool accepted, string message) = await synchronization.SynchronizeAsync(targetAddress, characterId);
            if (!accepted)
                return (false, message);
        }

        return (true, string.Empty);
    }

    /// <summary>RETRY on a row whose automatic publish failed (ET-245). No confirmation, unlike PUBLISH: the setting that
    /// published it without asking is the pilot's answer already, and the row reports the outcome itself.</summary>
    private Task _RetryPublishAsync(ActivityOverviewRowViewModel row) =>
        row.GroupCode is { } groupCode && _autoPublisher is { } publisher ? publisher.RetryAsync(groupCode) : Task.CompletedTask;

    private async Task<string?> _SelectServerAsync(IReadOnlyList<string> servers, IServerRegistry? registry, string prompt)
    {
        var options = new List<ServerPickOption>();
        foreach (string address in servers)
            options.Add(new ServerPickOption(address, registry is null ? address : await registry.DisplayNameAsync(address)));
        return await _dialogs.SelectServerAsync(prompt, options);
    }

    /// <summary>
    /// What the pilot is about to hand over, named rather than summarised as "this run will be shared". A run is not
    /// a fit: a fit is a list of modules, a run is what you earned, what you flew and where you were. Someone who
    /// presses publish has to know they are telling a server operator their location.
    /// </summary>
    private static string _WhatTravels(int runCount, string serverName) =>
        $"{runCount} of your runs go to {serverName}. Three things travel with them.\n\n"
        + "What you earned — every loot line with its item, quantity and price, and every bounty payout.\n"
        + "The fit you flew — by name.\n"
        + "Where you were — the solar system, and the signature if the run recorded one.\n\n"
        + "The operator of that server can read all of it. Other pilots see it only if they flew this activity with you.";

    /// <summary>Both sinks, one message: the screen's own status line for the reader who is looking at it, and a toast
    /// for the one who moved on. Two different texts for one outcome is how a rejection goes unnoticed.</summary>
    private void _ReportPublish(string message, ToastKind kind, string title = "Publish to server")
    {
        StatusMessage = message;
        _services.GetService<IToastService>()?.Show(title, message, kind);
    }

    // ── Picking in the strip, and the range line's ◀ ✕ ▶ (ET-292) ─────────────────────────────────────────────────

    private DateOnly _MonthInView => DateOnly.FromDateTime(ViewedMonthLocal);

    private DayOfWeek _FirstDay => _weekStart?.FirstDay ?? WeekStartService.SystemDefault();

    private static DateOnly _Today => DateOnly.FromDateTime(DateTime.Now);

    /// <summary>A click on a day in the strip: picks it, or — on the day already picked — goes back to the month. The
    /// day stays unfolded either way.</summary>
    public Task ToggleDayAsync(DateOnly day)
    {
        if (RangeKind == RunsRangeKind.Day && RangeStart == day)
        {
            ClearRange();
            return Task.CompletedTask;
        }

        return PickDayAsync(day);
    }

    /// <summary>A click on a week segment: picks the week, or lets the picked one go.</summary>
    public Task ToggleWeekAsync(DateOnly weekStart)
    {
        if (RangeKind == RunsRangeKind.Week && RangeStart == weekStart)
        {
            ClearRange();
            return Task.CompletedTask;
        }

        return PickWeekAsync(weekStart);
    }

    /// <summary>
    /// The range line on this day and its totals, the day unfolded and scrolled to the top of the list — and nothing
    /// else touched: the list keeps the whole month and every other day keeps its fold (besluit Jithran, 15 Sep).
    /// A day in another month brings that month into view first, so there is a header to scroll to.
    /// </summary>
    public async Task PickDayAsync(DateOnly day)
    {
        RangeKind = RunsRangeKind.Day;
        RangeStart = day;
        var month = new DateOnly(day.Year, day.Month, 1);
        if (month != _MonthInView)
            await _GoToMonthAsync(month, keepRange: true);
        else
            _RefreshRange();

        _Reveal([day]);
    }

    /// <summary>The week's totals on the range line, and its days with runs unfolded with the first of them in the list
    /// — the newest, since the list runs newest first — at the top, so the week reads downwards. The month in view
    /// stays where it is while the week touches it; otherwise it becomes the month of the week's first day with runs.</summary>
    public async Task PickWeekAsync(DateOnly weekStart)
    {
        RangeKind = RunsRangeKind.Week;
        RangeStart = weekStart;
        DateOnly[] days = [.. Enumerable.Range(0, 7).Select(weekStart.AddDays)];
        DateOnly monthInView = _MonthInView;
        if (!days.Any(day => day.Year == monthInView.Year && day.Month == monthInView.Month))
        {
            DateOnly anchor = days.FirstOrDefault(_tabDays.ContainsKey, weekStart);
            await _GoToMonthAsync(new DateOnly(anchor.Year, anchor.Month, 1), keepRange: true);
        }
        else
        {
            _RefreshRange();
        }

        _Reveal(days);
    }

    [RelayCommand]
    public void ClearRange()
    {
        RangeKind = RunsRangeKind.Month;
        _RefreshRange();
    }

    /// <summary>◀: the month before, or the previous day or week that has runs on this tab.</summary>
    [RelayCommand(CanExecute = nameof(CanGoPrevious))]
    private Task PreviousAsync() => _StepAsync(-1);

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private Task NextAsync() => _StepAsync(1);

    private Task _StepAsync(int direction)
    {
        switch (RangeKind)
        {
            case RunsRangeKind.Day when _StepDay(direction) is { } day:
                return PickDayAsync(day);
            case RunsRangeKind.Week when _StepWeek(direction) is { } week:
                return PickWeekAsync(week);
            case RunsRangeKind.Month:
                return _GoToMonthAsync(_MonthInView.AddMonths(direction));
            default:
                return Task.CompletedTask;
        }
    }

    /// <summary>The nearest day with runs before or after the picked one, among what the last read brought (the strip
    /// and the month in view).</summary>
    private DateOnly? _StepDay(int direction)
    {
        IEnumerable<DateOnly> candidates = direction < 0
            ? _tabDays.Keys.Where(day => day < RangeStart).OrderDescending()
            : _tabDays.Keys.Where(day => day > RangeStart).Order();
        return candidates.Cast<DateOnly?>().FirstOrDefault();
    }

    private DateOnly? _StepWeek(int direction)
    {
        DayOfWeek firstDay = _FirstDay;
        IEnumerable<DateOnly> candidates = direction < 0
            ? _tabDays.Keys.Where(day => day < RangeStart).OrderDescending()
            : _tabDays.Keys.Where(day => day >= RangeStart.AddDays(7)).Order();
        return candidates.Select(day => (DateOnly?)WeekMath.StartOf(day, firstDay)).FirstOrDefault();
    }

    private Task _GoToMonthAsync(DateOnly month, bool keepRange = false)
    {
        if (!keepRange)
            RangeKind = RunsRangeKind.Month;
        ViewedMonthLocal = month.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local);
        _RefreshRange();
        return _ReadAsync(null, withAutoSave: false);
    }

    /// <summary>Unfolds these days where the selected tab has them and asks the view to bring the first of them in the
    /// list to the top.</summary>
    private void _Reveal(IEnumerable<DateOnly> days)
    {
        if (SelectedTab is not { } tab)
            return;

        IReadOnlyList<RunsDayViewModel> expanded =
            tab.ExpandDays(days.Select(day => day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local)));
        if (expanded.Count > 0)
            DayScrollRequested?.Invoke(expanded[0]);
    }

    /// <summary>What one read has to cover: the twelve weeks of the strip and the month in view, whichever reaches
    /// further either way. Local days; the read converts local midnight to UTC (ET-233).</summary>
    private (DateOnly From, DateOnly To) _ReadRange()
    {
        DateOnly month = _MonthInView;
        DateOnly stripStart = RunsActivityStripViewModel.StartFor(_Today, month, _FirstDay);
        DateOnly stripEnd = stripStart.AddDays(7 * RunsActivityStripViewModel.Weeks);
        DateOnly monthEnd = month.AddMonths(1);
        return (stripStart < month ? stripStart : month, stripEnd > monthEnd ? stripEnd : monthEnd);
    }

    /// <summary>A week start changed while the screen is open (ET-297): the strip re-lays itself from what is already
    /// loaded. A picked week goes — the same dates are a different week now — and a picked day stays. Read again only
    /// when the grid now reaches past what the last read covered.</summary>
    private void _OnWeekStartChanged(DayOfWeek firstDay)
    {
        // WEEK stays open over the change: its range, title and DAYS re-lay under the new start (ET-294).
        RunsRangeKind? chosenScope = Summary.ChosenScope ?? (RangeKind == RunsRangeKind.Week ? RunsRangeKind.Week : null);
        if (RangeKind == RunsRangeKind.Week)
            RangeKind = RunsRangeKind.Month;
        Summary.ChosenScope = chosenScope;
        _RefreshRange();

        (DateOnly from, DateOnly to) = _ReadRange();
        if (from < _loadedFrom || to > _loadedTo)
            _ = _ReadAsync(null, withAutoSave: false);
    }

    /// <summary>The strip and the range line, drawn again from what is loaded — never a read.</summary>
    private void _RefreshRange()
    {
        RunsTabViewModel? tab = SelectedTab;
        _tabDays = tab is null
            ? new Dictionary<DateOnly, IReadOnlyList<RunsActivityFacts>>()
            : _FactsOf(tab)
                .Where(_PassesFilters)
                .GroupBy(activity => activity.Day)
                .ToDictionary(day => day.Key, day => (IReadOnlyList<RunsActivityFacts>)[.. day]);

        Strip.Show(new RunsStripInput(_FirstDay, _Today, _firstTracked, _MonthInView, RangeKind, RangeStart, _tabDays));

        List<IRunsActivityFigures> figures;
        switch (RangeKind)
        {
            case RunsRangeKind.Day:
                figures = [.. _tabDays.GetValueOrDefault(RangeStart) ?? []];
                RangeTitleText = RangeStart.ToString("dddd d MMMM", CultureInfo.InvariantCulture).ToUpperInvariant();
                PreviousTooltip = "Previous day with runs";
                NextTooltip = "Next day with runs";
                CanGoPrevious = _StepDay(-1) is not null;
                CanGoNext = _StepDay(1) is not null;
                break;
            case RunsRangeKind.Week:
                figures = [.. Enumerable.Range(0, 7).SelectMany(offset => _tabDays.GetValueOrDefault(RangeStart.AddDays(offset)) ?? [])];
                RangeTitleText = WeekMath.RangeText(RangeStart);
                PreviousTooltip = "Previous week with runs";
                NextTooltip = "Next week with runs";
                CanGoPrevious = _StepWeek(-1) is not null;
                CanGoNext = _StepWeek(1) is not null;
                break;
            default:
                // The month's own rows, every day of them whether folded or not: a month is always complete, and so
                // is its total (ET-233).
                figures = [.. tab?.Days.SelectMany(day => day.Rows) ?? []];
                RangeTitleText = MonthHeaderText;
                PreviousTooltip = "Previous month";
                NextTooltip = "Next month";
                CanGoPrevious = true;
                CanGoNext = true;
                break;
        }

        RangeCountText = RunsActivitySummaryText.ActivitiesCount(figures.Count);
        RangeFlownText = RunsActivitySummaryText.FlownFor(figures);
        RangeNetText = RunsActivitySummaryText.NetFor(figures);
        RangeIsk = RunsActivitySummaryText.SourcesFor(figures);
        _ShowSummary();
    }

    // ── TYPES and CHARACTERS filters (ET-293) ────────────────────────────────────────────────────────────────────

    private bool _PassesTypeFilter(RunTypeId typeId) => _excludedTypes.Count == 0 || !_excludedTypes.Contains(typeId);

    /// <summary>Empty excluded set: every row passes, including one with no own character on it at all — a group-mate's
    /// activity on a server tab, say. The moment one tile is off, only a row carrying a still-on own character does
    /// (Jithran, 15 Sep): a crew id this machine does not own never counts towards "on" on its own.</summary>
    private bool _PassesCharacterFilter(IReadOnlyList<long> crewCharacterIds)
    {
        if (_excludedCharacters.Count == 0)
            return true;

        foreach (long characterId in crewCharacterIds)
            if (_namesById.ContainsKey(characterId) && !_excludedCharacters.Contains(characterId))
                return true;

        return false;
    }

    private bool _PassesFilters(ActivityOverviewRowViewModel row) =>
        _PassesTypeFilter(row.TypeId) && _PassesCharacterFilter(row.CrewCharacterIds);

    private bool _PassesFilters(RunsActivityFacts facts) =>
        _PassesTypeFilter(facts.TypeId) && _PassesCharacterFilter(facts.CrewCharacterIds);

    /// <summary>Local holds every activity the local read built; a server tab what its own read brought (ET-311) — a
    /// group mate's run included, as that server's own rule hands it to anyone holding a run in the group.</summary>
    private IEnumerable<ActivityOverviewRowViewModel> _RowsOf(RunsTabViewModel tab) =>
        tab.ServerAddress is { } address
            ? _serverReads.GetValueOrDefault(address)?.Rows.Select(pair => pair.Row) ?? []
            : _loadedRows;

    private IEnumerable<RunsActivityFacts> _FactsOf(RunsTabViewModel tab) =>
        tab.ServerAddress is { } address ? _serverReads.GetValueOrDefault(address)?.Facts ?? [] : _loadedFacts;

    /// <summary>Every tab's days, rebuilt from its own rows with the TYPES/CHARACTERS filter applied —
    /// never a read of its own, so a tile toggle is as cheap as folding a day (ET-287).</summary>
    private void _ApplyFiltersToTabs()
    {
        foreach (RunsTabViewModel tab in Tabs)
        {
            ActivityOverviewRowViewModel[] tabRows = [.. _RowsOf(tab)];
            tab.Show([.. tabRows.Where(_PassesFilters)], _canPublish, IsPublishingMany);
            tab.StatusMessage = tab.Days.Count > 0
                ? null
                : tabRows.Length > 0 ? "No activity matches the current filters." : _EmptyMessageFor(tab);
        }

        // PUBLISH n LOCAL on the range line (RO-6): the selected tab's own local count, re-read every time a day's
        // could have changed — a filter toggle, a live refresh, or IsPublishingMany flipping.
        OnPropertyChanged(nameof(LocalInViewCount));
        OnPropertyChanged(nameof(ShowPublishView));
        OnPropertyChanged(nameof(PublishViewButtonText));
    }

    /// <summary>A tile toggled, soloed, or SHOW ALL: the tabs, the selection, the range line, the strip and the tiles
    /// themselves all follow, without reading anything again.</summary>
    private void _AfterFilterChanged()
    {
        _ApplyFiltersToTabs();
        _SettleSelection();
        _RefreshRange();
        _RefreshFilterTiles();
    }

    private void _ToggleType(object key)
    {
        var typeId = (RunTypeId)key;
        if (!_excludedTypes.Remove(typeId))
            _excludedTypes.Add(typeId);
        _AfterFilterChanged();
    }

    private void _SoloType(object key)
    {
        var typeId = (RunTypeId)key;
        _excludedTypes.Clear();
        _excludedTypes.UnionWith(TypeFilter.Tiles.Select(tile => (RunTypeId)tile.Key).Where(other => other != typeId));
        _AfterFilterChanged();
    }

    private void _ToggleCharacter(object key)
    {
        var characterId = (long)key;
        if (!_excludedCharacters.Remove(characterId))
            _excludedCharacters.Add(characterId);
        _AfterFilterChanged();
    }

    private void _SoloCharacter(object key)
    {
        var characterId = (long)key;
        _excludedCharacters.Clear();
        _excludedCharacters.UnionWith(_namesById.Keys.Where(other => other != characterId));
        _AfterFilterChanged();
    }

    /// <summary>The tiles of both blocks, drawn again from <see cref="_loadedRows"/> for the selected tab — never a
    /// read. Reconciled by <see cref="RunFilterTileViewModel.Key"/> rather than rebuilt (ET-287): a live refresh must
    /// not drop a hovered or focused tile just because the counts under it moved.</summary>
    private void _RefreshFilterTiles()
    {
        RunsTabViewModel? tab = SelectedTab;
        ActivityOverviewRowViewModel[] tabRows = tab is null ? [] : [.. _RowsOf(tab)];

        _ShowTypeTiles(tabRows);
        _ShowCharacterTiles(tabRows);
    }

    /// <summary>Every <see cref="RunTypeId"/> this tab's rows carry, in catalogue order — a count of 0 never happens
    /// here, unlike the mockup's own note about it: a type with nothing in this tab gets no tile at all rather than
    /// one reading zero (§RO-4: "types die in het geladen bereik voorkomen"). Counted against the CHARACTERS filter,
    /// never against its own — turning a type off must not also erase every other type's own count.</summary>
    private void _ShowTypeTiles(IReadOnlyList<ActivityOverviewRowViewModel> tabRows)
    {
        ActivityOverviewRowViewModel[] scoped = [.. tabRows.Where(row => _PassesCharacterFilter(row.CrewCharacterIds))];
        Dictionary<RunTypeId, int> counts = scoped.GroupBy(row => row.TypeId).ToDictionary(group => group.Key, group => group.Count());
        List<RunTypeId> order = [.. RunTypeCatalogue.All.Select(definition => definition.Id).Distinct()
            .Where(typeId => tabRows.Any(row => row.TypeId == typeId))];

        Dictionary<object, RunFilterTileViewModel> shown = TypeFilter.Tiles.ToDictionary(tile => tile.Key);
        List<RunFilterTileViewModel> tiles = [];
        foreach (RunTypeId typeId in order)
        {
            RunTypeDefinition definition = RunTypeCatalogue.For(typeId);
            RunFilterTileViewModel tile = shown.TryGetValue(typeId, out RunFilterTileViewModel? existing)
                ? existing
                : new RunFilterTileViewModel(typeId, definition.Name, definition.Icon, null, _ToggleType, _SoloType);
            tile.Count = counts.GetValueOrDefault(typeId);
            tile.IsOn = !_excludedTypes.Contains(typeId);
            tiles.Add(tile);
        }

        TypeFilter.Tiles.ReconcileTo(tiles);
        _ShowSummary(TypeFilter, tiles);
    }

    /// <summary>This machine's own characters, by name — counted against the TYPES filter, never its own, for the
    /// same reason <see cref="_ShowTypeTiles"/> counts against CHARACTERS.</summary>
    private void _ShowCharacterTiles(IReadOnlyList<ActivityOverviewRowViewModel> tabRows)
    {
        ActivityOverviewRowViewModel[] scoped = [.. tabRows.Where(row => _PassesTypeFilter(row.TypeId))];
        Dictionary<object, RunFilterTileViewModel> shown = CharacterFilter.Tiles.ToDictionary(tile => tile.Key);
        List<RunFilterTileViewModel> tiles = [];
        foreach ((long characterId, string name) in _namesById.OrderBy(pair => pair.Value, StringComparer.OrdinalIgnoreCase))
        {
            RunFilterTileViewModel tile = shown.TryGetValue(characterId, out RunFilterTileViewModel? existing)
                ? existing
                : new RunFilterTileViewModel(characterId, name, null, _FaceOf(characterId, name), _ToggleCharacter, _SoloCharacter);
            tile.Count = scoped.Count(row => row.CrewCharacterIds.Contains(characterId));
            tile.IsOn = !_excludedCharacters.Contains(characterId);
            tiles.Add(tile);
        }

        CharacterFilter.Tiles.ReconcileTo(tiles);
        _ShowSummary(CharacterFilter, tiles);
    }

    private static void _ShowSummary(RunFilterBlockViewModel block, IReadOnlyList<RunFilterTileViewModel> tiles)
    {
        int on = tiles.Count(tile => tile.IsOn);
        block.SummaryText = on < tiles.Count ? $"{on} of {tiles.Count}" : null;
    }

    private static DateTime _MonthStart(DateTime local) => new(local.Year, local.Month, 1, 0, 0, 0, DateTimeKind.Local);

    /// <summary>The month's bounds in UTC, from local midnight on its first day up to (exclusive) local midnight on
    /// the first day of the next one — the same local-then-convert order <c>HomeDashboardViewModel</c>'s "ISK today"
    /// boundary already uses for a day, so a run just after local midnight on the 1st never reads as the month
    /// before it.</summary>
    private static (DateTime FromUtc, DateTime ToUtcExclusive) _MonthRangeUtc(DateTime monthStartLocal) =>
        (monthStartLocal.ToUniversalTime(), monthStartLocal.AddMonths(1).ToUniversalTime());

    /// <summary>Text only, never a read: the clocks count from starts the last read already brought.</summary>
    private void _OnClockTick(object? sender, EventArgs e)
    {
        DateTime nowUtc = DateTime.UtcNow;
        foreach (RunningLaneViewModel lane in Lanes)
            lane.Tick(nowUtc);
        foreach (RunningGroupViewModel group in RunningGroups)
            group.Tick(nowUtc);
    }

    private string _NameOf(long characterId) => _characterNames.NameOf(characterId);

    /// <summary>One face per character for the whole screen, own and external alike: the hex shows the initial until
    /// the portrait lands, best-effort and fire-and-forget the same as a fleet roster leaf (ET-184, ET-306) — posted
    /// to the UI thread regardless of which thread creates the face, since a crew face is as likely to be minted from
    /// the pane's off-thread detail read as from the UI thread building the running lanes.</summary>
    private CharacterFaceViewModel _FaceOf(long characterId, string name) =>
        _faces.GetOrAdd(characterId, id =>
        {
            var face = new CharacterFaceViewModel(id, name);
            if (_services.GetService<ICharacterPortraitProvider>() is { } portraits)
                Dispatcher.UIThread.Post(() => _ = face.LoadPortraitAsync(portraits));
            return face;
        });

    /// <summary>The pilots' runs behind one row, read through the detail query rather than a read path of this screen's
    /// own — ET-160 owns what an activity's runs are, and a second answer here could disagree with the detail screen
    /// the same row opens. Off the UI thread: that query is several round trips and a price lookup.</summary>
    private async Task _LoadSubRunsAsync(ActivityOverviewRowViewModel row)
    {
        (IReadOnlyList<ActivityRunRowViewModel> runs, string? status) =
            await Task.Run<(IReadOnlyList<ActivityRunRowViewModel>, string?)>(async () =>
            {
                Result<ActivityDetailDto> detail = await _DetailOfAsync(row);
                if (!detail.IsSuccess || detail.Value is not { } activity)
                    return ([], detail.Messages.Count > 0 ? detail.Messages[0].Text : "The runs could not be read.");

                await _characterNames.HydrateAsync(activity.Runs.Select(run => run.CharacterId));
                return ([.. activity.Runs.OrderBy(run => run.StartedAtUtc).Select(run => new ActivityRunRowViewModel(run, _NameOf,
                    _FaceOf(run.CharacterId, CharacterNameResolver.Resolve(run.CharacterNameSnapshot, run.CharacterId, _NameOf)),
                    activity.IskByCharacter?.GetValueOrDefault(run.CharacterId), row))], null);
            });

        row.ShowSubRuns(runs, status);
    }

    /// <summary>A row is the way into ET-162's detail screen. The screen reads itself once it is routed, so nothing is
    /// fetched here.</summary>
    private Task _OpenDetailAsync(ActivityOverviewRowViewModel row)
    {
        // A server copy is nobody's to correct or delete there (ET-214/215); the expanded row and the pane are its detail.
        if (!row.CanOpenDetail)
            return Task.CompletedTask;

        _dialogs.ShowActivityDetail(
            new ActivityDetailViewModel(_dispatcher, row.ActivitySummaryId,
                _services.GetService<IAppraisalProvider>(), _NameOf,
                _services.GetService<IEsiClient>(), _services.GetService<IEsiLocationClient>(),
                _services.GetService<ISdeAccessor>(), _services.GetService<ICharacterPortraitProvider>(),
                _services.GetService<ITypeImageProvider>(),
                // Only this machine's own pilots' runs can be corrected there, or deleted from there (ET-214):
                // anyone else's came in from a server and could never be published back (ET-215).
                _namesById.Keys.ToHashSet(),
                _canPublish ? () => _PublishAsync(row) : null, _dialogs, _services.GetService<RunChangeFeed>(),
                _services),
            row.ActivitySummaryId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// A character's avatar in RUNNING, or their running line's OPEN. Idle, it is the one-click start (Jithran,
    /// ET-290): the manual start screen with exactly this character in it and no picker. Type and site are chosen
    /// there — the type comes preset from that screen's own memory — because a run needs both, and one started on a
    /// guessed site gets the wrong name. This screen never starts a run itself.
    /// </summary>
    private async Task _ActOnLaneAsync(RunningLaneViewModel lane)
    {
        if (lane.Run is { } run)
        {
            _OpenRunWindow(run, lane.CharacterText);
            return;
        }

        if (_services.GetService<ISdeAccessor>() is not { } sde)
            return;

        await _dialogs.ShowManualRunStartAsync(new ManualRunStartViewModel(_dispatcher, sde, _dialogs,
            kind => new ActivityWindowViewModel(kind, _services), [lane.Character],
            preselectedCharacter: lane.Character, toasts: _services.GetService<IToastService>(),
            fleetParticipation: _services.GetService<IFleetParticipation>(),
            localPresence: _services.GetService<ILocalCharacterPresence>(), isCharacterFixed: true));
    }

    private Task _OpenRunningGroupAsync(RunningGroupViewModel group)
    {
        if (group.Lanes.FirstOrDefault(lane => lane.Run is not null) is { Run: { } run } first)
            _OpenRunWindow(run, first.CharacterText);
        return Task.CompletedTask;
    }

    /// <summary>The run window adopts the stored running run itself, so it only has to be opened; it is also the one
    /// place that owns STOP and SAVE. Named first (ET-221): with two groups running, a window left to find "the one run
    /// running" would find neither.</summary>
    private void _OpenRunWindow(RunningRunDto run, string characterName)
    {
        var window = new ActivityWindowViewModel(run.ActivityKind, _services);
        window.UseCharacter(checked((int)run.CharacterId), characterName);
        _dialogs.ShowActivityWindow(window);
    }

    /// <summary>START RUN ▾: the general start, the same as Tools → Start run — every character offered (ET-221), none
    /// preselected, so the screen's own default applies: the fleet, or the team last picked (ET-270).</summary>
    [RelayCommand]
    private async Task OpenRunStartAsync()
    {
        if (_services.GetService<ISdeAccessor>() is not { } sde)
            return;

        ICharacterRegistry registry = _services.GetRequiredService<ICharacterRegistry>();
        IReadOnlyList<Character> characters = await Task.Run(() => registry.GetAllAsync());
        await _dialogs.ShowManualRunStartAsync(new ManualRunStartViewModel(_dispatcher, sde, _dialogs,
            kind => new ActivityWindowViewModel(kind, _services), characters,
            toasts: _services.GetService<IToastService>(),
            fleetParticipation: _services.GetService<IFleetParticipation>(),
            localPresence: _services.GetService<ILocalCharacterPresence>()));
    }

    public void Dispose()
    {
        _runChangesSubscription?.Dispose();
        if (_weekStart is not null)
            _weekStart.Changed -= _OnWeekStartChanged;
        if (_clock is null)
            return;

        _clock.Stop();
        _clock.Tick -= _OnClockTick;
    }

    private sealed record ScreenReadRequest(
        DateTime MonthLocal,
        DateOnly ReadFrom,
        DateOnly ReadTo,
        IReadOnlyDictionary<Guid, ActivityOverviewRowViewModel> ShownRows,
        IReadOnlyDictionary<string, IReadOnlyDictionary<Guid, ActivityOverviewRowViewModel>> ShownServerRows,
        IReadOnlyDictionary<string, string> ServerHeaders,
        bool WithAutoSave,
        RunChangeBatch? Changed);

    private sealed record RunningRunFacts(RunningRunDto Run, string TypeText, string? SystemText);

    private sealed record ScreenRead(
        IReadOnlyList<RunningRunFacts> Running,
        IReadOnlyList<UnfinishedRunDto> Unfinished,
        IReadOnlyList<(string Address, string Header)> NewServers,
        bool CanPublish,
        IReadOnlyList<(ActivityOverviewRowViewModel Row, ActivityOverviewRowViewModel? Previous)>? Rows,
        string? OverviewError,
        bool FleetHistoryKnownEmpty,
        IReadOnlyList<RunsActivityFacts> Facts,
        DateOnly? FirstTracked,
        bool OverviewSkipped,
        /// <summary>Each server tab's own read (ET-311), when this pass read the servers; null keeps what they showed.</summary>
        IReadOnlyDictionary<string, ServerTabRead>? ServerReads = null);

    /// <summary>What one server tab shows (ET-311): its rows for the month, its facts for the strip, or the reason it
    /// shows neither — that nobody is connected to it, or what its read said.</summary>
    private sealed record ServerTabRead(
        IReadOnlyList<(ActivityOverviewRowViewModel Row, ActivityOverviewRowViewModel? Previous)> Rows,
        IReadOnlyList<RunsActivityFacts> Facts,
        string? Error);
}
