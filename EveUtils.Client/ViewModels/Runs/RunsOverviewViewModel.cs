using System;
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
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Queries;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Transport;
using Microsoft.Extensions.DependencyInjection;
using CqrsDispatcher = EveUtils.Shared.Cqrs.IDispatcher;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>
/// ET-161: the runs screen in the shell — the band of running runs on top, then one row per finished activity,
/// grouped under a day band. Until this existed a saved run left the screen the moment its window closed and there
/// was nowhere in the app to see what you flew yesterday.
///
/// <para><b>The width this is designed at, and why.</b> Jithran's open question — design at the start size or at the
/// window as it actually stands — is answered here as <i>the start size, 758px</i>, and the answer is a choice, not
/// a measurement. Three reasons. <c>ModuleHostService.Render</c> moves this very <c>Content</c> between a docked tab
/// and a floating window, so there is exactly one layout and its binding constraint is the narrowest width it must
/// survive; 758 is that width and it is the default, not an edge case. "The window as it stands" is not one number —
/// docked and floating differ by 342px and no two operators keep the same size — so designing to it is designing to
/// nothing. And elastic layout is free here: star columns, a wrapping chip strip and character-ellipsis trimming
/// cost nothing at 1180 and are the whole of what makes 758 work. Wider is therefore not a second design but the
/// same one with more room, which is also why no element is folded behind a "⋯": an overflow menu would hide, at
/// the default width, exactly the rewards AC-3 says may never disappear.</para>
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
    private bool _canPublish;

    /// <summary>Set once per load when <see cref="_fleetFilter"/> is active and turned up nothing: whether that
    /// empty result is a real zero or an unknowable one (ET-185) — read by <see cref="_FillTab"/> so every tab's
    /// empty message says the same thing rather than each guessing from its own row count.</summary>
    private bool _fleetHistoryKnownEmpty;

    /// <summary>The month on screen (ET-233), the first of that month at local midnight. Local, not UTC, because the
    /// day bands below it already group by local day (ET-98) — a month that disagreed with its own days about where
    /// midnight falls would put an activity in a band that says one date under a header that says another.
    /// Untouched by a live refresh (<see cref="_RefreshAsync"/>): only <see cref="PreviousMonthAsync"/> and
    /// <see cref="NextMonthAsync"/> move it, so a run landing in the current month while an older one is on screen
    /// never pulls the reader back to it.</summary>
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
        _namesById = characters
            .Where(character => character.EsiCharacterId is > 0)
            .GroupBy(character => (long)character.EsiCharacterId!.Value)
            .ToDictionary(group => group.Key, group => group.First().Name);
        SelectedTab = LocalTab;

        // A lane per local character, running or not — the roster is the band. Each lane asks GetRunningRunsQuery
        // (ET-203) which run is running for ITS character, so two characters running at once already show two live
        // lanes here; that query has no "exactly one" rule to hit, unlike the single-run GetRunningRunQuery a run
        // window uses to reopen (ET-130 is only about lifting the one-app-wide limit that query's callers still have).
        Lanes = [.. characters
            .Where(character => character.EsiCharacterId is > 0)
            .Select(character => new RunningLaneViewModel(character, _ActOnLaneAsync))];
        LanesEmptyText = Lanes.Count == 0
            ? "No character is linked yet, so there is no lane to run one on."
            : null;

        // Best-effort, fire-and-forget per lane — same pattern as a fleet roster leaf (FleetsViewModel) and the
        // character picker (ET-184): the hex shows the glyph fallback until the render lands, rather than the whole
        // screen waiting on a network call for a portrait that ET-200 only asks to reuse, not to gate on.
        if (services.GetService<ICharacterPortraitProvider>() is { } portraits)
            foreach (RunningLaneViewModel lane in Lanes)
                _ = lane.LoadPortraitAsync(portraits);

        // One subscription for every way a run can change (ET-222). This screen used to hold six, one per event, each
        // added when the gap before it was found (ET-189, ET-203, ET-220) and each knowing which part to reload — the
        // next command or event was always one more pairing nobody remembered. The feed hands the change over on the
        // UI thread and folds a burst of payouts into one read, so all that is left here is what to read again.
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

    /// <summary>RUNNING and UNFINISHED show only here. A lane is a clock running on this machine and an unfinished run
    /// is a decision owed on it; neither is something a server holds, so neither belongs under a server's name.</summary>
    public bool IsLocalTabSelected => SelectedTab?.IsLocal ?? true;

    /// <summary>Whether there is anything to choose between. A lone "Local" tab is a label for a choice that does not
    /// exist, so the strip stays away until a server is coupled — the fit browser's rule.</summary>
    [ObservableProperty] private bool _hasServerTabs;

    private RunsTabViewModel LocalTab => Tabs[0];

    public ObservableCollection<RunningLaneViewModel> Lanes { get; }

    /// <summary>Stopped and never finished — their own band, above the days and outside them (ET-179).</summary>
    public ObservableCollection<UnfinishedRunViewModel> UnfinishedRuns { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowUnfinishedBand))]
    private bool _hasUnfinishedRuns;

    public bool ShowUnfinishedBand => HasUnfinishedRuns && IsLocalTabSelected;

    /// <summary>Why the band is empty, when it is. Null once there is at least one lane.</summary>
    public string? LanesEmptyText { get; }

    /// <summary>"Runs for 'Woensdag Homefronts'" when opened from a fleet's RUNS button (ET-185), null otherwise —
    /// so the header says what narrowed the list down, rather than the screen quietly showing fewer runs than the
    /// reader expects from the app's one runs screen.</summary>
    public string? FleetFilterText => _fleetFilter is { } filter ? $"Runs for '{filter.FleetName}'" : null;

    /// <summary>Why nothing is listed — a failed read and an empty history are different things and say so.</summary>
    [ObservableProperty] private string? _statusMessage;

    public void RefreshModule() => _ = LoadAsync();

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await _LoadLanesAsync(cancellationToken);
        // The deadline is judged here as well as at startup: this screen is where a day-old stopped run would
        // otherwise sit and be offered as unfinished long after it stopped being that (ET-179).
        await _dispatcher.Send(new SaveRunsLeftUnfinishedCommand(DateTime.UtcNow), cancellationToken);
        await _LoadUnfinishedRunsAsync(cancellationToken);

        await _RefreshServerTabsAsync(cancellationToken);
        await _FillTabsAsync(null, cancellationToken);
    }

    /// <summary>Everything on this screen a run can change, read again in place (ET-222): the lanes, UNFINISHED and
    /// every tab's days. Not the auto-save <see cref="LoadAsync"/> runs first — a refresh answers a change and never
    /// makes one. Already on the UI thread, handed over by <see cref="RunChangeFeed"/>, so no token: nothing is in
    /// flight for one to cancel.</summary>
    private async Task _RefreshAsync(RunChangeBatch changed)
    {
        await _LoadLanesAsync(CancellationToken.None);
        await _LoadUnfinishedRunsAsync(CancellationToken.None);
        await _FillTabsAsync(changed, CancellationToken.None);
    }

    /// <summary>Reads the overview and brings every tab's day bands in line with it.</summary>
    /// <param name="changed">What moved, when this answers a change; null reads every open row's runs again.</param>
    private async Task _FillTabsAsync(RunChangeBatch? changed, CancellationToken cancellationToken)
    {
        (DateTime fromUtc, DateTime toUtc) = _MonthRangeUtc(ViewedMonthLocal);
        Result<IReadOnlyList<ActivityOverviewRowDto>> overview = await _dispatcher.Query(
            new GetActivityOverviewQuery(fromUtc, toUtc, FleetId: _fleetFilter?.FleetId), cancellationToken);
        if (!overview.IsSuccess || overview.Value is null)
        {
            // What is on screen stays: a read that failed says nothing about what the days hold.
            StatusMessage = overview.Messages.Count > 0 ? overview.Messages[0].Text : "The activities could not be read.";
            return;
        }

        StatusMessage = null;
        // Only worth asking when the fleet filter came up empty — a coverage query a full result already answers by
        // existing (ET-185: GetFleetRunCoverageQuery is the "why is this empty" question, not the "what is here" one).
        _fleetHistoryKnownEmpty = overview.Value.Count > 0 || _fleetFilter is null || await _IsFleetHistoryKnownEmptyAsync(
            _fleetFilter, cancellationToken);
        List<Task> subRunReads = [];
        foreach (RunsTabViewModel tab in Tabs)
            _FillTab(tab, overview.Value, changed, subRunReads);
        await Task.WhenAll(subRunReads);
    }

    /// <summary>
    /// The activities this tab stands for: everything on Local, and on a server tab the ones whose runs carry that
    /// server's address — including a group-mate's run, which the sync merged into the local database as its own row
    /// under their character id.
    ///
    /// A run someone else flew SOLO on that server is not here, and that is the server's own rule rather than a gap
    /// in this filter: <c>ServerRunSyncRepository.ListChangedAsync</c> hands back a run only to a character who holds
    /// a run in the same group, so the server never tells us about it and no screen can show it.
    ///
    /// Reconciled rather than rebuilt (ET-222): a day already on screen is the same band afterwards, open or folded as
    /// the reader left it (ET-189), and a row whose figures did not move is the same row — which is what keeps an
    /// opened row open and the scroll offset where it was while payouts land every few seconds. An activity is
    /// recognised by its summary id, which a rebuild keeps (ET-215).
    /// </summary>
    private void _FillTab(RunsTabViewModel tab, IReadOnlyList<ActivityOverviewRowDto> overview, RunChangeBatch? changed,
        List<Task> subRunReads)
    {
        List<ActivityOverviewRowDto> rows = tab.ServerAddress is { } address
            ? [.. overview.Where(row => row.ServerSyncStates.Any(state => state.ServerAddress == address))]
            : [.. overview];
        Dictionary<Guid, ActivityOverviewRowViewModel> shownRows = tab.Days.SelectMany(day => day.Rows)
            .ToDictionary(row => row.ActivitySummaryId);
        Dictionary<DateTime, RunsDayViewModel> shownDays = tab.Days.ToDictionary(day => day.Day);

        List<RunsDayViewModel> days = [];
        foreach (IGrouping<DateTime, ActivityOverviewRowViewModel> day in rows
                     .Select(row => _RowFor(row, shownRows, changed, subRunReads))
                     .GroupBy(row => row.StartedAtLocal.Date))
        {
            if (shownDays.TryGetValue(day.Key, out RunsDayViewModel? shown))
            {
                shown.Show([.. day]);
                days.Add(shown);
            }
            else
                days.Add(new RunsDayViewModel(day.Key, [.. day]));
        }

        tab.Days.ReconcileTo(days);

        // Opens on the most recent day only (ET-199), so he never has to scroll through weeks of history to reach
        // today; every older evening still says its piece collapsed, via RunsWindow's sectionsummary in the band
        // itself. A day already on screen keeps whatever the reader set for it — this default only ever reaches a
        // day this tab is showing for the first time, the evening's first save included. Applies the same way when
        // browsing to a different month: that month's own most recent day opens, not "today".
        if (days.MaxBy(day => day.Day) is { } latest && !shownDays.ContainsKey(latest.Day))
            latest.IsExpanded = true;

        tab.StatusMessage = tab.Days.Count > 0 ? null : _EmptyMessageFor(tab);
        // The month total (ET-233): the same NetFor formula each day band already sums its own rows with, over
        // every row this tab holds regardless of which days are folded — a month is always complete, so its total
        // never depends on what the reader happens to have open.
        tab.UpdateMonthSummary();
    }

    /// <summary>The row already on screen when it still says the same, a new one in its place when it does not. An
    /// unchanged row that is open has its runs read again when the change reached them: a pilot's share or a
    /// corrected time moves a run without moving the activity's figures.</summary>
    private ActivityOverviewRowViewModel _RowFor(ActivityOverviewRowDto row,
        IReadOnlyDictionary<Guid, ActivityOverviewRowViewModel> shownRows, RunChangeBatch? changed, List<Task> subRunReads)
    {
        shownRows.TryGetValue(row.ActivitySummaryId, out ActivityOverviewRowViewModel? shown);
        RunPublishProgress? progress = _autoPublisher?.ProgressFor(row.GroupCode);
        if (shown is not null && shown.IsShowing(row, _canPublish, progress))
        {
            if (shown.IsExpanded && (changed is null || changed.Concerns(row.RunId is { } runId ? [runId] : [], row.GroupCode)))
                subRunReads.Add(_LoadSubRunsAsync(shown));
            return shown;
        }

        var fresh = new ActivityOverviewRowViewModel(row, _NameOf, _LoadSubRunsAsync, _OpenDetailAsync,
            _canPublish ? _PublishAsync : null, _ServerNameOf, progress, _RetryPublishAsync);
        if (shown is not null)
            subRunReads.Add(fresh.ContinueFromAsync(shown));
        return fresh;
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
    private async Task<bool> _IsFleetHistoryKnownEmptyAsync(RunsFleetFilter filter, CancellationToken cancellationToken)
    {
        Result<FleetRunCoverageDto> coverage = await _dispatcher.Query(
            new GetFleetRunCoverageQuery(filter.FleetId, filter.FleetCreatedAtUtc), cancellationToken);
        return coverage.IsSuccess && coverage.Value is { IsKnown: true };
    }

    /// <summary>Adds a tab for a server coupled since this screen was built, never rebuilding the strip: the reader's
    /// chosen tab must survive a refresh. Which is also why a decoupled server keeps its tab until the screen is
    /// reopened — the activities on it are still true.</summary>
    private async Task _RefreshServerTabsAsync(CancellationToken cancellationToken)
    {
        IClientSessionStore? sessionStore = _services.GetService<IClientSessionStore>();
        if (sessionStore is null)
            return;

        IServerRegistry? registry = _services.GetService<IServerRegistry>();
        IReadOnlyList<string> servers = await sessionStore.ListServersAsync(cancellationToken);
        _canPublish = servers.Count > 0;
        foreach (string address in servers)
        {
            if (Tabs.Any(tab => tab.ServerAddress == address))
                continue;

            string header = registry is null ? address : await registry.DisplayNameAsync(address);
            Tabs.Add(new RunsTabViewModel(header, address));
        }
        HasServerTabs = Tabs.Count > 1;
    }

    private async Task _LoadLanesAsync(CancellationToken cancellationToken)
    {
        Result<IReadOnlyList<RunningRunDto>> running = await _dispatcher.Query(new GetRunningRunsQuery(), cancellationToken);
        IReadOnlyList<RunningRunDto> runs = running.IsSuccess ? running.Value ?? [] : [];
        DateTime nowUtc = DateTime.UtcNow;
        foreach (RunningLaneViewModel lane in Lanes)
            lane.Attach(runs.FirstOrDefault(run => (long?)lane.Character.EsiCharacterId == run.CharacterId), nowUtc);
    }

    private async Task _LoadUnfinishedRunsAsync(CancellationToken cancellationToken)
    {
        Result<IReadOnlyList<UnfinishedRunDto>> unfinished =
            await _dispatcher.Query(new GetUnfinishedRunsQuery(), cancellationToken);
        Dictionary<Guid, UnfinishedRunViewModel> shown = UnfinishedRuns.ToDictionary(run => run.RunId);
        UnfinishedRuns.ReconcileTo([.. (unfinished.Value ?? []).Select(run =>
            shown.TryGetValue(run.RunId, out UnfinishedRunViewModel? same) && same.IsShowing(run)
                ? same
                : new UnfinishedRunViewModel(run, _NameOf(run.CharacterId), _SaveUnfinishedRunAsync,
                    _DeleteUnfinishedRunAsync, _ResumeUnfinishedRunAsync))]);
        HasUnfinishedRuns = UnfinishedRuns.Count > 0;
    }

    /// <summary>Commit the run as it stands. Nothing is handed along: what the run window watched died with it, and
    /// the loot captures and bounty lines it wrote as they came in are already on the row.</summary>
    private async Task _SaveUnfinishedRunAsync(UnfinishedRunViewModel run)
    {
        DateTime nowUtc = DateTime.UtcNow;
        Result saved = await _dispatcher.Send(
            new SaveRunCommand(run.RunId, run.StoppedAtUtc ?? nowUtc, nowUtc, [], [], [], []));
        await _AfterFinishingAsync(saved, "The run could not be saved.");
    }

    private async Task _DeleteUnfinishedRunAsync(UnfinishedRunViewModel run)
    {
        if (!await _dialogs.ConfirmAsync("Throw this run away?",
                $"{run.SiteText} on {run.CharacterText} goes, with the loot and bounty recorded on it. "
                + "Saving keeps it instead.", "Delete"))
            return;

        Result deleted = await _dispatcher.Send(new DeleteRunCommand(run.RunId, DateTime.UtcNow));
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

        IReadOnlyList<string> servers = await sessionStore.ListServersAsync();
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

        Result<ActivityDetailDto> detail = await _dispatcher.Query(new GetActivityDetailQuery(row.ActivitySummaryId));
        if (!detail.IsSuccess || detail.Value is null)
        {
            _ReportPublish(detail.Messages.Count > 0 ? detail.Messages[0].Text : "The activity could not be read.", ToastKind.Error);
            return;
        }

        IReadOnlyList<ClientSessionTokens> coupled = await sessionStore.LoadAllAsync(targetAddress);
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

        foreach (ActivityRunDetailDto run in ownRuns)
        {
            Result queued = await _dispatcher.Send(new QueueRunForServerSyncCommand(run.RunId, targetAddress));
            if (queued.IsSuccess)
                continue;

            _ReportPublish(queued.Messages.Count > 0 ? queued.Messages[0].Text : "The run could not be queued.", ToastKind.Error);
            return;
        }

        (bool accepted, string message) = await _SynchronizeAsync(targetAddress, ownRuns);

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

    /// <summary>A server by the name its tab carries, so a row and the tab it is filed under say the same thing.</summary>
    private string _ServerNameOf(string serverAddress) =>
        Tabs.FirstOrDefault(tab => tab.ServerAddress == serverAddress)?.Header ?? serverAddress;

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
        return _FillTabsAsync(null, CancellationToken.None);
    }

    private static DateTime _MonthStart(DateTime local) => new(local.Year, local.Month, 1, 0, 0, 0, DateTimeKind.Local);

    /// <summary>The month's bounds in UTC, from local midnight on its first day up to (exclusive) local midnight on
    /// the first day of the next one — the same local-then-convert order <c>HomeDashboardViewModel</c>'s "ISK today"
    /// boundary already uses for a day, so a run just after local midnight on the 1st never reads as the month
    /// before it.</summary>
    private static (DateTime FromUtc, DateTime ToUtcExclusive) _MonthRangeUtc(DateTime monthStartLocal) =>
        (monthStartLocal.ToUniversalTime(), monthStartLocal.AddMonths(1).ToUniversalTime());

    private void _OnClockTick(object? sender, EventArgs e)
    {
        DateTime nowUtc = DateTime.UtcNow;
        foreach (RunningLaneViewModel lane in Lanes)
            lane.Tick(nowUtc);
    }

    private string _NameOf(long characterId) =>
        _namesById.TryGetValue(characterId, out string? name) ? name : $"character {characterId}";

    /// <summary>The deelruns behind one row, read through the detail query rather than a read path of this screen's
    /// own — ET-160 owns what an activity's runs are, and a second answer here could disagree with the detail
    /// screen the same row opens.</summary>
    private async Task _LoadSubRunsAsync(ActivityOverviewRowViewModel row)
    {
        Result<ActivityDetailDto> detail = await _dispatcher.Query(new GetActivityDetailQuery(row.ActivitySummaryId));
        row.SubRuns.Clear();
        if (!detail.IsSuccess || detail.Value is null)
        {
            row.SubRunsStatus = detail.Messages.Count > 0 ? detail.Messages[0].Text : "The runs could not be read.";
            return;
        }

        row.SubRunsStatus = null;
        foreach (ActivityRunDetailDto run in detail.Value.Runs.OrderBy(run => run.StartedAtUtc))
            row.SubRuns.Add(new ActivityRunRowViewModel(run, _NameOf));
    }

    /// <summary>The row is the only way into ET-162's detail screen; nothing else in the app reaches it. The screen
    /// reads itself once it is routed, so nothing is fetched here.</summary>
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

    private async Task _ActOnLaneAsync(RunningLaneViewModel lane)
    {
        if (lane.Run is { } run)
        {
            // The run window adopts the stored running run itself, so it only has to be opened; it is also the one
            // place that owns STOP and SAVE, which is why this lane does not carry a second copy of either.
            _dialogs.ShowActivityWindow(new ActivityWindowViewModel(run.ActivityKind, _services));
        }
        else if (_services.GetService<ISdeAccessor>() is { } sde)
        {
            // Every registered character is offered (ET-221), same as Tools → Start run — but this lane's own
            // character starts ticked, so the dialog opens on the card the operator pressed rather than on
            // whoever happens to sort first, and others can still be added alongside it.
            IReadOnlyList<Character> characters =
                await _services.GetRequiredService<ICharacterRegistry>().GetAllAsync();
            await _dialogs.ShowManualRunStartAsync(new ManualRunStartViewModel(_dispatcher, sde, _dialogs,
                kind => new ActivityWindowViewModel(kind, _services), characters,
                preselectedCharacter: lane.Character, toasts: _services.GetService<IToastService>(),
                fleetParticipation: _services.GetService<IFleetParticipation>(),
                localPresence: _services.GetService<ILocalCharacterPresence>()));
        }
    }

    public void Dispose()
    {
        _runChangesSubscription?.Dispose();
        if (_clock is null)
            return;

        _clock.Stop();
        _clock.Tick -= _OnClockTick;
    }
}
