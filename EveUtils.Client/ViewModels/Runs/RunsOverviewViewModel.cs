using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
/// one fixed height at every width; what does not fit is trimmed, chips included, and the activity pane (RO-2) is where
/// all of it can be read. The day headers, activity rows and pilots' runs are one flat, virtualised sequence per tab
/// (<see cref="RunsTabViewModel.Items"/>): folding a day takes its rows out of it, since expanders nested inside a list
/// would put every row back in the visual tree.</para>
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
    /// Untouched by a live refresh: only <see cref="PreviousMonthAsync"/> and <see cref="NextMonthAsync"/> move it,
    /// so a run landing in the current month while an older one is on screen never pulls the reader back to it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MonthHeaderText))]
    private DateTime _viewedMonthLocal = _MonthStart(DateTime.Now);

    public string MonthHeaderText => ViewedMonthLocal.ToString("MMMM yyyy", CultureInfo.InvariantCulture).ToUpperInvariant();

    public RunsOverviewViewModel(CqrsDispatcher dispatcher, IDialogService dialogs, IServiceProvider services,
        IReadOnlyList<Character> characters, bool runClock = true, RunsFleetFilter? fleetFilter = null)
    {
        _dispatcher = dispatcher;
        _dialogs = dialogs;
        _services = services;
        _fleetFilter = fleetFilter;
        _facts = new RunRowFacts(services.GetService<ISdeAccessor>());
        _namesById = characters
            .Where(character => character.EsiCharacterId is > 0)
            .GroupBy(character => (long)character.EsiCharacterId!.Value)
            .ToDictionary(group => group.Key, group => group.First().Name);
        SelectedTab = LocalTab;

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

        // Best-effort and fire-and-forget, the same as a fleet roster leaf and the character picker (ET-184): the hex
        // shows the initial until the portrait lands. One face per character, shared by every place that draws them.
        if (services.GetService<ICharacterPortraitProvider>() is { } portraits)
            foreach (RunningLaneViewModel lane in Lanes)
                _ = lane.Face.LoadPortraitAsync(portraits);

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
    public ObservableCollection<RunsTabViewModel> Tabs { get; } = [new("Local", null)];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLocalTabSelected))]
    [NotifyPropertyChangedFor(nameof(ShowUnfinishedBand))]
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
        var request = new ScreenReadRequest(ViewedMonthLocal,
            LocalTab.Days.SelectMany(day => day.Rows).ToDictionary(row => row.ActivitySummaryId),
            Tabs.Where(tab => tab.ServerAddress is not null).ToDictionary(tab => tab.ServerAddress!, tab => tab.Header),
            withAutoSave);
        ScreenRead read = await Task.Run(() => _ReadScreenAsync(request));

        _ShowRunning(read.Running);
        _ShowUnfinished(read.Unfinished);
        foreach ((string address, string header) in read.NewServers)
            if (Tabs.All(tab => tab.ServerAddress != address))
                Tabs.Add(new RunsTabViewModel(header, address));
        HasServerTabs = Tabs.Count > 1;
        _canPublish = read.CanPublish;

        if (read.Rows is null)
        {
            // What is on screen stays: a read that failed says nothing about what the days hold.
            StatusMessage = read.OverviewError;
            return;
        }

        StatusMessage = null;
        _fleetHistoryKnownEmpty = read.FleetHistoryKnownEmpty;
        List<Task> subRunReads = [];
        foreach ((ActivityOverviewRowViewModel row, ActivityOverviewRowViewModel? previous) in read.Rows)
        {
            if (ReferenceEquals(row, previous))
            {
                // An unchanged row that is open has its runs read again when the change reached them: a pilot's share
                // or a corrected time moves a run without moving the activity's figures.
                if (row.IsExpanded && (changed is null || changed.Concerns(row.RunId is { } runId ? [runId] : [], row.GroupCode)))
                    subRunReads.Add(_LoadSubRunsAsync(row));
                continue;
            }

            row.LayoutChanged += _OnRowLayoutChanged;
            if (previous is not null)
                subRunReads.Add(row.ContinueFromAsync(previous));
        }

        ActivityOverviewRowViewModel[] rows = [.. read.Rows.Select(pair => pair.Row)];
        foreach (RunsTabViewModel tab in Tabs)
        {
            // Local holds every activity; a server tab the ones whose runs carry that server's address — including a
            // group-mate's run, which the sync merged into the local database as its own row under their character id.
            // A run someone else flew SOLO on that server is not here, and that is the server's own rule:
            // ServerRunSyncRepository.ListChangedAsync hands a run only to a character who holds a run in its group.
            tab.Show(tab.ServerAddress is { } address ? [.. rows.Where(row => row.IsPublishedTo(address))] : rows);
            tab.StatusMessage = tab.Days.Count > 0 ? null : _EmptyMessageFor(tab);
        }

        await Task.WhenAll(subRunReads);
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

        (DateTime fromUtc, DateTime toUtc) = _MonthRangeUtc(request.MonthLocal);
        Result<IReadOnlyList<ActivityOverviewRowDto>> overview = await _dispatcher.Query(
            new GetActivityOverviewQuery(fromUtc, toUtc, FleetId: _fleetFilter?.FleetId));
        if (!overview.IsSuccess || overview.Value is null)
            return new ScreenRead(runningFacts, unfinished.Value ?? [], newServers, canPublish, null,
                overview.Messages.Count > 0 ? overview.Messages[0].Text : "The activities could not be read.", false);

        string ServerNameOf(string address) => headers.GetValueOrDefault(address) ?? address;
        List<(ActivityOverviewRowViewModel Row, ActivityOverviewRowViewModel? Previous)> rows = [];
        foreach (ActivityOverviewRowDto dto in overview.Value)
        {
            request.ShownRows.TryGetValue(dto.ActivitySummaryId, out ActivityOverviewRowViewModel? shown);
            RunPublishProgress? progress = _autoPublisher?.ProgressFor(dto.GroupCode);
            ActivityOverviewRowViewModel row = shown is not null && shown.IsShowing(dto, canPublish, progress)
                ? shown
                : new ActivityOverviewRowViewModel(dto, _NameOf, _LoadSubRunsAsync, _OpenDetailAsync,
                    canPublish ? _PublishAsync : null, ServerNameOf, progress, _RetryPublishAsync, _facts, _FaceOf);
            rows.Add((row, shown));
        }

        // Only worth asking when the fleet filter came up empty — a coverage query a full result already answers by
        // existing (ET-185: GetFleetRunCoverageQuery is the "why is this empty" question, not the "what is here" one).
        bool fleetHistoryKnownEmpty = overview.Value.Count > 0 || _fleetFilter is null
            || await _IsFleetHistoryKnownEmptyAsync(_fleetFilter);
        return new ScreenRead(runningFacts, unfinished.Value ?? [], newServers, canPublish, rows, null, fleetHistoryKnownEmpty);
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
        string? targetAddress = servers.Count == 1 ? servers[0] : await _SelectServerAsync(servers, registry, row);
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

        (bool accepted, string message) = await Task.Run(() => _SynchronizeAsync(targetAddress, ownRuns));

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

    /// <summary>One synchronisation per owning character: the server attributes a push to the session it came in on,
    /// so two of this machine's pilots in the same activity are two pushes, not one. Stops at the first refusal —
    /// what the server said about it is worth more than a second attempt's message.</summary>
    private async Task<(bool Accepted, string Message)> _SynchronizeAsync(
        string targetAddress, IReadOnlyList<ActivityRunDetailDto> ownRuns)
    {
        using IServiceScope scope = _services.CreateScope();
        RunSynchronizationService synchronization = scope.ServiceProvider.GetRequiredService<RunSynchronizationService>();
        foreach (long characterId in ownRuns.Select(run => run.CharacterId).Distinct())
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

    private async Task<string?> _SelectServerAsync(
        IReadOnlyList<string> servers, IServerRegistry? registry, ActivityOverviewRowViewModel row)
    {
        var options = new List<ServerPickOption>();
        foreach (string address in servers)
            options.Add(new ServerPickOption(address, registry is null ? address : await registry.DisplayNameAsync(address)));
        return await _dialogs.SelectServerAsync($"Publish '{row.SiteText}' to which server?", options);
    }

    /// <summary>
    /// What the pilot is about to hand over, named rather than summarised as "this run will be shared". A run is not
    /// a fit: a fit is a list of modules, a run is what you earned, what you flew and where you were. Someone who
    /// presses publish has to know they are telling a server operator their location.
    /// </summary>
    private static string _WhatTravels(int runCount, string serverName) =>
        $"{runCount} of your runs in this activity go to {serverName}. Three things travel with them.\n\n"
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

    [RelayCommand]
    private Task PreviousMonthAsync() => _GoToMonthAsync(ViewedMonthLocal.AddMonths(-1));

    [RelayCommand]
    private Task NextMonthAsync() => _GoToMonthAsync(ViewedMonthLocal.AddMonths(1));

    private Task _GoToMonthAsync(DateTime monthLocal)
    {
        ViewedMonthLocal = monthLocal;
        return _ReadAsync(null, withAutoSave: false);
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

    private string _NameOf(long characterId) =>
        _namesById.TryGetValue(characterId, out string? name) ? name : $"character {characterId}";

    /// <summary>One face per character for the whole screen: this machine's own come with a portrait, anyone else
    /// with their initial.</summary>
    private CharacterFaceViewModel _FaceOf(long characterId, string name) =>
        _faces.GetOrAdd(characterId, id => new CharacterFaceViewModel(id, name));

    /// <summary>The pilots' runs behind one row, read through the detail query rather than a read path of this screen's
    /// own — ET-160 owns what an activity's runs are, and a second answer here could disagree with the detail screen
    /// the same row opens. Off the UI thread: that query is several round trips and a price lookup.</summary>
    private async Task _LoadSubRunsAsync(ActivityOverviewRowViewModel row)
    {
        Guid activityId = row.ActivitySummaryId;
        (IReadOnlyList<ActivityRunRowViewModel> runs, string? status) =
            await Task.Run<(IReadOnlyList<ActivityRunRowViewModel>, string?)>(async () =>
            {
                Result<ActivityDetailDto> detail = await _dispatcher.Query(new GetActivityDetailQuery(activityId));
                if (!detail.IsSuccess || detail.Value is not { } activity)
                    return ([], detail.Messages.Count > 0 ? detail.Messages[0].Text : "The runs could not be read.");

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
        if (_clock is null)
            return;

        _clock.Stop();
        _clock.Tick -= _OnClockTick;
    }

    private sealed record ScreenReadRequest(
        DateTime MonthLocal,
        IReadOnlyDictionary<Guid, ActivityOverviewRowViewModel> ShownRows,
        IReadOnlyDictionary<string, string> ServerHeaders,
        bool WithAutoSave);

    private sealed record RunningRunFacts(RunningRunDto Run, string TypeText, string? SystemText);

    private sealed record ScreenRead(
        IReadOnlyList<RunningRunFacts> Running,
        IReadOnlyList<UnfinishedRunDto> Unfinished,
        IReadOnlyList<(string Address, string Header)> NewServers,
        bool CanPublish,
        IReadOnlyList<(ActivityOverviewRowViewModel Row, ActivityOverviewRowViewModel? Previous)>? Rows,
        string? OverviewError,
        bool FleetHistoryKnownEmpty);
}
