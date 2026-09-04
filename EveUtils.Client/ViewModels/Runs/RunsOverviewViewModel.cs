using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Messaging;
using EveUtils.Client.Notifications;
using EveUtils.Client.Runs;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Market.Repositories;
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
    private bool _canPublish;

    public RunsOverviewViewModel(CqrsDispatcher dispatcher, IDialogService dialogs, IServiceProvider services,
        IReadOnlyList<Character> characters, bool runClock = true)
    {
        _dispatcher = dispatcher;
        _dialogs = dialogs;
        _services = services;
        _namesById = characters
            .Where(character => character.EsiCharacterId is > 0)
            .GroupBy(character => (long)character.EsiCharacterId!.Value)
            .ToDictionary(group => group.Key, group => group.First().Name);
        SelectedTab = LocalTab;

        // A lane per local character, running or not — the roster is the band, and today it happens to hold at most
        // one running run because RunningRunLookup answers only when there is exactly one (ET-130 is what lifts that).
        Lanes = [.. characters
            .Where(character => character.EsiCharacterId is > 0)
            .Select(character => new RunningLaneViewModel(character, _ActOnLaneAsync))];
        LanesEmptyText = Lanes.Count == 0
            ? "No character is linked yet, so there is no lane to run one on."
            : null;

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

        Result<IReadOnlyList<ActivityOverviewRowDto>> overview =
            await _dispatcher.Query(new GetActivityOverviewQuery(), cancellationToken);
        foreach (RunsTabViewModel tab in Tabs)
            tab.Days.Clear();
        if (!overview.IsSuccess || overview.Value is null)
        {
            StatusMessage = overview.Messages.Count > 0 ? overview.Messages[0].Text : "The activities could not be read.";
            return;
        }

        StatusMessage = null;
        foreach (RunsTabViewModel tab in Tabs)
            _FillTab(tab, overview.Value);
    }

    /// <summary>
    /// The activities this tab stands for: everything on Local, and on a server tab the ones whose runs carry that
    /// server's address — including a group-mate's run, which the sync merged into the local database as its own row
    /// under their character id.
    ///
    /// A run someone else flew SOLO on that server is not here, and that is the server's own rule rather than a gap
    /// in this filter: <c>ServerRunSyncRepository.ListChangedAsync</c> hands back a run only to a character who holds
    /// a run in the same group, so the server never tells us about it and no screen can show it.
    /// </summary>
    private void _FillTab(RunsTabViewModel tab, IReadOnlyList<ActivityOverviewRowDto> overview)
    {
        List<ActivityOverviewRowDto> rows = tab.ServerAddress is { } address
            ? [.. overview.Where(row => row.ServerSyncStates.Any(state => state.ServerAddress == address))]
            : [.. overview];

        foreach (IGrouping<DateTime, ActivityOverviewRowViewModel> day in rows
                     .Select(row => new ActivityOverviewRowViewModel(row, _NameOf, _LoadSubRunsAsync, _OpenDetailAsync,
                         _canPublish ? _PublishAsync : null))
                     .GroupBy(row => row.StartedAtLocal.Date))
            tab.Days.Add(new RunsDayViewModel(day.Key, [.. day]));

        tab.StatusMessage = tab.Days.Count > 0
            ? null
            : tab.IsLocal
                ? "No activity has been saved yet. A run shows up here the moment you save it."
                : "Nothing published to this server yet. Publish an activity from Local to put it here.";
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
        Result<RunningRunDto> running = await _dispatcher.Query(new GetRunningRunQuery(), cancellationToken);
        RunningRunDto? run = running.IsSuccess ? running.Value : null;
        DateTime nowUtc = DateTime.UtcNow;
        foreach (RunningLaneViewModel lane in Lanes)
            lane.Attach(run is not null && (long?)lane.Character.EsiCharacterId == run.CharacterId ? run : null, nowUtc);
    }

    private async Task _LoadUnfinishedRunsAsync(CancellationToken cancellationToken)
    {
        Result<IReadOnlyList<UnfinishedRunDto>> unfinished =
            await _dispatcher.Query(new GetUnfinishedRunsQuery(), cancellationToken);
        UnfinishedRuns.Clear();
        foreach (UnfinishedRunDto run in unfinished.Value ?? [])
            UnfinishedRuns.Add(new UnfinishedRunViewModel(run, _NameOf(run.CharacterId),
                _SaveUnfinishedRunAsync, _DeleteUnfinishedRunAsync));
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
                _services.GetService<IMarketPriceRepository>(), _NameOf),
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
            // Handed only this pilot, so the dialog opens on the lane the operator pressed rather than on whoever
            // happens to sort first.
            await _dialogs.ShowManualRunStartAsync(new ManualRunStartViewModel(_dispatcher, sde, _dialogs,
                kind => new ActivityWindowViewModel(kind, _services), [lane.Character]));
    }

    public void Dispose()
    {
        if (_clock is null)
            return;

        _clock.Stop();
        _clock.Tick -= _OnClockTick;
    }
}
