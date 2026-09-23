using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Client.Calendar;
using EveUtils.Client.Messaging;
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
    private readonly RunRowFacts _facts;
    private readonly IDisposable? _runChanges;
    private readonly DispatcherTimer? _clock;

    private bool _isReadingRuns;
    private bool _isRunsReadOwed;

    /// <summary>Design-time: no services, empty blocks.</summary>
    public HomeDashboardViewModel()
    {
        _facts = new RunRowFacts(null);
        Earnings = new HomeEarningsViewModel((_, _) => { });
    }

    public HomeDashboardViewModel(IServiceProvider services, HomeNavigation navigation)
    {
        _dispatcher = services.GetService<CqrsDispatcher>();
        _registry = services.GetService<ICharacterRegistry>();
        _weekStart = services.GetService<IWeekStartService>();
        _serverStatus = services.GetService<EveServerStatusService>();
        _busConnector = services.GetService<IRemoteBusConnector>();
        _facts = new RunRowFacts(services.GetService<ISdeAccessor>());

        Earnings = new HomeEarningsViewModel((kind, start) => _ = navigation.OpenRuns(runs => _PickRangeAsync(runs, kind, start)));

        _runChanges = services.GetService<RunChangeFeed>()?.Subscribe(_ => ReadRunsAsync());
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

    [ObservableProperty] private string _clockText = string.Empty;
    [ObservableProperty] private string _tranquilityText = "Tranquility";
    [ObservableProperty] private bool _isTranquilityUp;
    [ObservableProperty] private string _serverText = "ET not coupled";
    [ObservableProperty] private bool _isServerConnected;
    [ObservableProperty] private bool _hasServer;

    /// <summary>Every block's first read — the one time the whole home is read.</summary>
    public async Task LoadAsync() => await ReadRunsAsync();

    /// <summary>The runs, read once for every block that shows them. Owed rather than overlapped (ET-287).</summary>
    public async Task ReadRunsAsync()
    {
        if (_dispatcher is null || _registry is null)
            return;

        if (_isReadingRuns)
        {
            _isRunsReadOwed = true;
            return;
        }

        _isReadingRuns = true;
        try
        {
            do
            {
                _isRunsReadOwed = false;
                DateTime nowLocal = DateTime.Now;
                DayOfWeek firstDay = _weekStart?.FirstDay ?? WeekStartService.SystemDefault();
                HomeRunsRead read = await Task.Run(() => _ReadRunsOffThreadAsync(nowLocal, firstDay));
                Earnings.Show(new HomeEarningsInput(read.Activities, nowLocal, firstDay, read.FirstTracked));
            }
            while (_isRunsReadOwed);
        }
        finally
        {
            _isReadingRuns = false;
        }
    }

    private async Task<HomeRunsRead> _ReadRunsOffThreadAsync(DateTime nowLocal, DayOfWeek firstDay)
    {
        IReadOnlyList<Character> characters = await _registry!.GetAllAsync();
        long[] ownIds = [.. characters.Where(character => character.EsiCharacterId is > 0).Select(character => (long)character.EsiCharacterId!.Value)];

        DateOnly today = DateOnly.FromDateTime(nowLocal);
        DateTime fromUtc = EarningsPeriods.ReadFrom(today, firstDay).ToDateTime(TimeOnly.MinValue, DateTimeKind.Local).ToUniversalTime();
        DateTime toUtc = today.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Local).ToUniversalTime();
        Result<IReadOnlyList<ActivityOverviewRowDto>> overview =
            await _dispatcher!.Query(new GetActivityOverviewQuery(fromUtc, toUtc, OwnCharacterIds: ownIds));
        Result<DateTime?> firstStart = await _dispatcher.Query(new GetFirstActivityStartQuery());

        IReadOnlyList<ActivityOverviewRowDto> rows = overview.IsSuccess ? overview.Value ?? [] : [];
        return new HomeRunsRead(
            [.. rows.Select(row => RunsActivityFacts.From(row, _facts))],
            firstStart is { IsSuccess: true, Value: { } firstUtc } ? DateOnly.FromDateTime(firstUtc.ToLocalTime()) : null);
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

    private void _OnWeekStartChanged(DayOfWeek firstDay) => _ = ReadRunsAsync();

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

    private void _OnClockTick(object? sender, EventArgs e) => _ShowClock();

    private void _ShowClock() =>
        ClockText = DateTime.Now.ToString("ddd d MMM · HH:mm", CultureInfo.InvariantCulture);

    public void Dispose()
    {
        _runChanges?.Dispose();
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

    private sealed record HomeRunsRead(IReadOnlyList<RunsActivityFacts> Activities, DateOnly? FirstTracked);
}
