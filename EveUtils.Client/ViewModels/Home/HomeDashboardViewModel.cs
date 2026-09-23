using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Client.Calendar;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Fleet;
using EveUtils.Client.Imaging;
using EveUtils.Client.Messaging;
using EveUtils.Client.Notifications;
using EveUtils.Client.Runs;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Esi.Status;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Queries;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Transport;
using Microsoft.Extensions.DependencyInjection;
using CqrsDispatcher = EveUtils.Shared.Cqrs.IDispatcher;

namespace EveUtils.Client.ViewModels.Home;

/// <summary>
/// The home screen (ET-324): what a pilot checks in the first ten seconds — anything broken, anything running, what
/// they made, where their toons are, their last runs, their fleets, new fits and what happened while away.
///
/// <para><b>Every block updates on its own.</b> A run change reads the runs once (earnings, pilots' ISK, latest runs,
/// the running lane) and touches nothing else; a fleet change reloads the fleets block only; presence, the game log and
/// the token tracker move single fields on a pilot row. There is no refresh of the whole home: that is what hung the
/// run window in ET-298, one full refresh per fleet metric.</para>
///
/// <para><b>Nothing is read on the UI thread.</b> Every query runs inside <c>Task.Run</c> — the SQLite provider's
/// async API is synchronous — and only the results are applied here. One runs read at a time; a change landing while
/// one is out is owed and read straight after, never dropped (ET-287).</para>
/// </summary>
public sealed partial class HomeDashboardViewModel : ObservableObject, IDisposable
{
    private readonly CqrsDispatcher? _dispatcher;
    private readonly ICharacterRegistry? _registry;
    private readonly IWeekStartService? _weekStart;
    private readonly EveServerStatusService? _serverStatus;
    private readonly IRemoteBusConnector? _busConnector;
    private readonly IToastService? _toasts;
    private readonly IClientSessionStore? _sessions;
    private readonly IServerRegistry? _serverRegistry;
    private readonly IExternalCharacterLookup? _externalCharacters;
    private readonly RunRowFacts _facts;
    private readonly CharacterFaceCache _faces;
    private readonly RunPublisher? _publisher;
    private readonly HomeNavigation _navigation;
    private readonly IDisposable? _runChanges;
    private readonly DispatcherTimer? _clock;

    private bool _isReadingRuns;
    private bool _isRunsReadOwed;
    private bool _isOwedReadFull;
    private DateTime _minuteShown;
    private (string Site, DateTime EndedUtc)? _lastRun;

    /// <summary>Design-time: no services, empty blocks.</summary>
    public HomeDashboardViewModel()
    {
        _facts = new RunRowFacts(null);
        _faces = new CharacterFaceCache(null);
        _navigation = HomeNavigation.None;
        Earnings = new HomeEarningsViewModel((_, _) => { });
        Pilots = new HomePilotsViewModel([], null, HomeNavigation.None);
        LatestRuns = new HomeLatestRunsViewModel(_ => Task.CompletedTask, () => { });
        Fleets = new HomeFleetsViewModel(null, HomeNavigation.None, _faces);
        Fits = new HomeFitsViewModel(null, HomeNavigation.None, null);
    }

    /// <param name="characters">The shell's own live character rows — presence, portraits and ESI state are kept
    /// there, and the pilot rows sit on top of them.</param>
    /// <param name="fitLibrary">The shell's own fit list, which the library line follows.</param>
    public HomeDashboardViewModel(IServiceProvider services, HomeNavigation navigation,
        ObservableCollection<CharacterViewModel> characters, INotifyCollectionChanged? fitLibrary = null)
    {
        _dispatcher = services.GetService<CqrsDispatcher>();
        _registry = services.GetService<ICharacterRegistry>();
        _weekStart = services.GetService<IWeekStartService>();
        _serverStatus = services.GetService<EveServerStatusService>();
        _busConnector = services.GetService<IRemoteBusConnector>();
        _toasts = services.GetService<IToastService>();
        _sessions = services.GetService<IClientSessionStore>();
        _serverRegistry = services.GetService<IServerRegistry>();
        _externalCharacters = services.GetService<IExternalCharacterLookup>();
        _facts = new RunRowFacts(services.GetService<ISdeAccessor>());
        _faces = new CharacterFaceCache(services.GetService<ICharacterPortraitProvider>());
        _navigation = navigation;

        Earnings = new HomeEarningsViewModel((kind, start) => _ = navigation.OpenRuns(runs => _PickRangeAsync(runs, kind, start)));
        Pilots = new HomePilotsViewModel(characters, services, navigation);
        LatestRuns = new HomeLatestRunsViewModel(_PublishAsync, () => _ = navigation.OpenRuns(null));
        Fleets = new HomeFleetsViewModel(services, navigation, _faces);
        Fits = new HomeFitsViewModel(services, navigation, fitLibrary);
        if (_dispatcher is not null && services.GetService<IDialogService>() is { } dialogs)
        {
            Running = new RunningBandViewModel(_dispatcher, dialogs, services, [], _faces.FaceOf);
            _publisher = new RunPublisher(_dispatcher, dialogs, services);
        }

        _runChanges = services.GetService<RunChangeFeed>()?.Subscribe(ReadRunsAsync);
        if (_weekStart is not null)
            _weekStart.Changed += _OnWeekStartChanged;
        if (_serverStatus is not null)
            _serverStatus.Changed += _OnServerStatusChanged;
        if (_busConnector is not null)
            _busConnector.StateChanged += _OnBusStateChanged;

        _ShowTranquility();
        _ShowServer();
        _ShowClock();
        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += _OnClockTick;
        _clock.Start();
    }

    public HomeEarningsViewModel Earnings { get; }

    public HomePilotsViewModel Pilots { get; }

    /// <summary>The runs overview's own RUNNING band; null without the services it starts runs through.</summary>
    public RunningBandViewModel? Running { get; }

    public HomeLatestRunsViewModel LatestRuns { get; }

    public HomeFleetsViewModel Fleets { get; }

    public HomeFitsViewModel Fits { get; }

    [ObservableProperty] private string _clockText = string.Empty;
    [ObservableProperty] private string _tranquilityText = "Tranquility";
    [ObservableProperty] private bool _isTranquilityUp;
    [ObservableProperty] private string _serverText = "ET not coupled";
    [ObservableProperty] private bool _isServerConnected;
    [ObservableProperty] private bool _hasServer;

    /// <summary>Every block's first read — the one time the whole home is read.</summary>
    public async Task LoadAsync()
    {
        await Task.WhenAll(ReadRunsAsync(null), Pilots.ReadQueuesAsync(), Fleets.ReadAsync(), Fits.LoadAsync());
    }

    /// <summary>
    /// The runs, read once for every block that shows them: earnings, the pilots' ISK, the running band, the latest
    /// runs and the best drops. One read at a time; a change landing meanwhile is owed and read straight after
    /// (ET-287). A batch that only touches running runs reads the band alone — a bounty landing on a running run moves
    /// nothing a saved run is counted in (the runs overview's rule, ET-292).
    /// </summary>
    public async Task ReadRunsAsync(RunChangeBatch? changed)
    {
        if (_dispatcher is not { } dispatcher || _registry is not { } registry)
            return;

        bool isFull = changed is null || !_OnlyRunning(changed);
        if (_isReadingRuns)
        {
            _isRunsReadOwed = true;
            _isOwedReadFull |= isFull;
            return;
        }

        _isReadingRuns = true;
        try
        {
            do
            {
                _isRunsReadOwed = false;
                _isOwedReadFull = false;
                DateTime nowLocal = DateTime.Now;
                DayOfWeek firstDay = _weekStart?.FirstDay ?? WeekStartService.SystemDefault();
                bool readAll = isFull;
                Dictionary<Guid, ActivityOverviewRowViewModel> shownRows = LatestRuns.Items.OfType<HomeRunLine>()
                    .ToDictionary(line => line.Row.ActivitySummaryId, line => line.Row);
                HomeRunsRead read = await Task.Run(() =>
                    _ReadRunsOffThreadAsync(dispatcher, registry, nowLocal, firstDay, readAll, shownRows));
                _ShowRuns(read, nowLocal, firstDay);
                isFull = _isOwedReadFull;
            }
            while (_isRunsReadOwed);
        }
        finally
        {
            _isReadingRuns = false;
        }
    }

    private bool _OnlyRunning(RunChangeBatch changed)
    {
        if (Running is not { } band || changed.IsUnscoped || changed.RunIds.Count == 0)
            return false;

        HashSet<Guid> running = [.. band.Lanes.Select(lane => lane.Run?.Id).OfType<Guid>()];
        HashSet<string> groups = [.. band.Lanes.Select(lane => lane.Run?.GroupCode).OfType<string>()];
        return changed.RunIds.All(running.Contains) && changed.GroupCodes.All(groups.Contains);
    }

    private async Task<HomeRunsRead> _ReadRunsOffThreadAsync(CqrsDispatcher dispatcher, ICharacterRegistry registry,
        DateTime nowLocal, DayOfWeek firstDay, bool readAll, IReadOnlyDictionary<Guid, ActivityOverviewRowViewModel> shownRows)
    {
        IReadOnlyList<Character> characters = await registry.GetAllAsync();
        IReadOnlyList<RunningRunFacts>? running = await RunningBandViewModel.ReadAsync(dispatcher, _facts);
        if (!readAll)
            return new HomeRunsRead(characters, running, null);

        long[] ownIds = [.. characters.Select(character => character.EsiCharacterId).OfType<int>().Where(id => id > 0).Select(id => (long)id)];
        DateOnly today = DateOnly.FromDateTime(nowLocal);
        DateTime fromUtc = EarningsPeriods.ReadFrom(today, firstDay).ToDateTime(TimeOnly.MinValue, DateTimeKind.Local).ToUniversalTime();
        DateTime toUtc = today.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Local).ToUniversalTime();
        Result<IReadOnlyList<ActivityOverviewRowDto>> overview =
            await dispatcher.Query(new GetActivityOverviewQuery(fromUtc, toUtc, OwnCharacterIds: ownIds));
        Result<DateTime?> firstStart = await dispatcher.Query(new GetFirstActivityStartQuery());
        Result<IReadOnlyList<Guid>> local = await dispatcher.Query(new GetLocalActivityIdsQuery(ownIds));
        Result<IReadOnlyList<BestDropDto>> drops = await dispatcher.Query(
            new GetBestDropsQuery(DateTime.UtcNow.AddDays(-7), ownIds, HomeLatestRunsViewModel.ShownDrops));
        IReadOnlyList<string> servers = _sessions is null ? [] : await _sessions.ListServersAsync();
        string? serverName = servers.Count == 1
            ? _serverRegistry is null ? servers[0] : await _serverRegistry.DisplayNameAsync(servers[0])
            : null;

        IReadOnlyList<ActivityOverviewRowDto> rows = overview.IsSuccess ? overview.Value ?? [] : [];
        var names = new RunsCharacterNames(
            characters.Where(character => character.EsiCharacterId is > 0)
                .GroupBy(character => (long)(character.EsiCharacterId ?? 0))
                .ToDictionary(group => group.Key, group => group.First().Name),
            _externalCharacters);
        List<ActivityOverviewRowViewModel> latest = [];
        foreach (ActivityOverviewRowDto dto in rows.Take(HomeLatestRunsViewModel.Shown))
            latest.Add(shownRows.TryGetValue(dto.ActivitySummaryId, out ActivityOverviewRowViewModel? shown)
                       && shown.IsShowing(dto, servers.Count > 0)
                ? shown
                : _NewRow(dto, names));

        return new HomeRunsRead(characters, running, new HomeRunsFacts(
            [.. rows.Select(row => RunsActivityFacts.From(row, _facts))],
            firstStart is { IsSuccess: true, Value: { } firstUtc } ? DateOnly.FromDateTime(firstUtc.ToLocalTime()) : null,
            ownIds.ToHashSet(),
            latest,
            local.IsSuccess ? local.Value ?? [] : [],
            drops.IsSuccess ? drops.Value ?? [] : [],
            servers.Count > 0,
            serverName));
    }

    /// <summary>A row as the runs overview builds it — off the UI thread, with the same facts cache — whose click opens
    /// the runs overview on that run.</summary>
    private ActivityOverviewRowViewModel _NewRow(ActivityOverviewRowDto dto, RunsCharacterNames names)
    {
        var row = new ActivityOverviewRowViewModel(dto, names.NameOf, _ => Task.CompletedTask, _ => Task.CompletedTask,
            facts: _facts, faceOf: _faces.FaceOf);
        row.SelectRequested += selected => _ = _navigation.OpenRuns(async runs =>
        {
            await runs.LoadAsync();
            await runs.OpenRunAsync(selected.ActivitySummaryId, DateOnly.FromDateTime(selected.StartedAtLocal));
        });
        return row;
    }

    private void _ShowRuns(HomeRunsRead read, DateTime nowLocal, DayOfWeek firstDay)
    {
        if (Running is { } band)
        {
            band.ShowCharacters(read.Characters);
            if (read.Running is { } running)
                band.Show(running);
        }

        if (read.Facts is not { } facts)
            return;

        Earnings.Show(new HomeEarningsInput(facts.Activities, nowLocal, firstDay, facts.FirstTracked));
        Pilots.ShowIsk(EarningsPeriods.ByCharacter(facts.Activities, nowLocal, facts.OwnCharacterIds), nowLocal);
        LatestRuns.Show(facts.Latest, facts.Activities.ToLookup(activity => activity.Day), DateOnly.FromDateTime(nowLocal),
            facts.LocalActivityIds, facts.CanPublish, facts.ServerName);
        LatestRuns.ShowDrops(facts.Drops);
        _lastRun = facts.Latest.Count > 0
            ? (facts.Latest[0].SiteText, (facts.Latest[0].StartedAtLocal + facts.Latest[0].Duration).ToUniversalTime())
            : null;
        _ShowIdleText();
    }

    /// <summary>"nothing running · last run Sansha Refuge ended 12 min ago" — the clock moves it, never a read.</summary>
    private void _ShowIdleText()
    {
        if (Running is not { } band)
            return;

        band.IdleText = _lastRun is { } last
            ? $"nothing running · last run {last.Site} ended {_Ago(DateTime.UtcNow - last.EndedUtc)}"
            : "nothing running";
    }

    private static string _Ago(TimeSpan ago) => ago.TotalMinutes switch
    {
        < 1 => "just now",
        < 60 => $"{(int)ago.TotalMinutes} min ago",
        < 60 * 24 => $"{(int)ago.TotalHours} h ago",
        _ => $"{(int)ago.TotalDays} d ago"
    };

    private async Task _PublishAsync(IReadOnlyList<Guid> activityIds)
    {
        if (_publisher is null)
            return;

        if (await _publisher.PublishManyAsync(activityIds, _ => { }) is { } outcome)
            _toasts?.Show(outcome.Title, outcome.Message, outcome.Kind);
    }

    private static async Task _PickRangeAsync(RunsOverviewViewModel runs, EarningsPeriodKind kind, DateOnly start)
    {
        switch (kind)
        {
            case EarningsPeriodKind.Today:
                await runs.PickDayAsync(start);
                break;
            case EarningsPeriodKind.Week:
                await runs.PickWeekAsync(start);
                break;
        }
    }

    private void _OnWeekStartChanged(DayOfWeek firstDay) => _ = ReadRunsAsync(null);

    private void _OnServerStatusChanged(EveServerStatusSnapshot snapshot) => Dispatcher.UIThread.Post(_ShowTranquility);

    private void _OnBusStateChanged(string address, ServerConnectionState state) => Dispatcher.UIThread.Post(_ShowServer);

    private void _ShowTranquility()
    {
        EveServerStatusSnapshot? snapshot = _serverStatus?.Current;
        IsTranquilityUp = snapshot?.State is EveServerState.Online or EveServerState.Vip;
        TranquilityText = snapshot switch
        {
            { State: EveServerState.Online, Players: { } players } => $"Tranquility {players.ToString("N0", CultureInfo.InvariantCulture)}",
            { State: EveServerState.Vip } => "Tranquility VIP",
            { State: EveServerState.Offline } => "Tranquility offline",
            _ => "Tranquility"
        };
    }

    private void _ShowServer()
    {
        IReadOnlyCollection<ServerConnectionState> states = _busConnector?.States.Values.ToArray() ?? [];
        HasServer = states.Count > 0;
        IsServerConnected = states.Any(state => state == ServerConnectionState.Connected);
        ServerText = !HasServer ? "ET not coupled" : IsServerConnected ? "ET connected" : "ET unreachable";
    }

    private void _OnClockTick(object? sender, EventArgs e)
    {
        DateTime nowUtc = DateTime.UtcNow;
        Running?.Tick(nowUtc);
        Pilots.Tick(nowUtc);
        if (nowUtc.Minute == _minuteShown.Minute && nowUtc - _minuteShown < TimeSpan.FromMinutes(1))
            return;

        // "Today" turns over at local midnight with or without a run, so the first tick of a new day reads again.
        bool isNewDay = _minuteShown != default && _minuteShown.ToLocalTime().Date != nowUtc.ToLocalTime().Date;
        _minuteShown = nowUtc;
        _ShowClock();
        _ShowIdleText();
        if (isNewDay)
            _ = ReadRunsAsync(null);
    }

    private void _ShowClock() =>
        ClockText = DateTime.Now.ToString("ddd d MMM · HH:mm", CultureInfo.InvariantCulture);

    public void Dispose()
    {
        _runChanges?.Dispose();
        Pilots.Dispose();
        Fleets.Dispose();
        Fits.Dispose();
        if (_weekStart is not null)
            _weekStart.Changed -= _OnWeekStartChanged;
        if (_serverStatus is not null)
            _serverStatus.Changed -= _OnServerStatusChanged;
        if (_busConnector is not null)
            _busConnector.StateChanged -= _OnBusStateChanged;
        if (_clock is null)
            return;

        _clock.Stop();
        _clock.Tick -= _OnClockTick;
    }

    /// <param name="Facts">Null for a read of the running band alone.</param>
    private sealed record HomeRunsRead(
        IReadOnlyList<Character> Characters,
        IReadOnlyList<RunningRunFacts>? Running,
        HomeRunsFacts? Facts);

    private sealed record HomeRunsFacts(
        IReadOnlyList<RunsActivityFacts> Activities,
        DateOnly? FirstTracked,
        IReadOnlySet<long> OwnCharacterIds,
        IReadOnlyList<ActivityOverviewRowViewModel> Latest,
        IReadOnlyList<Guid> LocalActivityIds,
        IReadOnlyList<BestDropDto> Drops,
        bool CanPublish,
        string? ServerName);
}
