using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Fittings;
using EveUtils.Client.Notifications;
using EveUtils.Client.ViewModels.FitBrowser;
using EveUtils.Client.Esi;
using EveUtils.Client.Skills;
using EveUtils.Client.Implants;
using EveUtils.Shared.Modules.Skills.Repositories;
using EveUtils.Shared.Modules.Implants.Repositories;
using EveUtils.Shared.Modules.Settings.Repositories;
using EveUtils.Client.Fleet;
using EveUtils.Client.Gamelog;
using EveUtils.Client.Imaging;
using EveUtils.Client.Messaging;
using EveUtils.Shared.Modules.Market.Repositories;
using EveUtils.Client.Pairing;
using EveUtils.Client.Theming;
using EveUtils.Client.Transport;
using EveUtils.Shared.Transport;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Esi;
using EveUtils.Shared.Modules.Esi.Http;
using EveUtils.Shared.Modules.Esi.Status;
using EveUtils.Shared.Modules.Fittings;
using EveUtils.Shared.Modules.Fleet.Metrics;
using EveUtils.Shared.Modules.Fittings.Dtos;
using EveUtils.Shared.Modules.Fittings.Services;
using EveUtils.Shared.Modules.Fittings.Commands;
using EveUtils.Shared.Modules.Fittings.Repositories;
using EveUtils.Shared.Modules.Fittings.Services.Parsers;
using EveUtils.Shared.Modules.Fittings.Events;
using EveUtils.Shared.Modules.Fittings.Queries;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Modules.Gamelog.Dtos;
using EveUtils.Shared.Modules.Gamelog.Events;
using EveUtils.Shared.Modules.Gamelog.Models;
using EveUtils.Shared.Modules.Gamelog.Reading;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Sde.Import;
using EveUtils.Shared.Modules.Settings.Commands;
using EveUtils.Shared.Modules.Settings.Dtos;
using EveUtils.Shared.Modules.Settings.Queries;
using EveUtils.Shared.Modules.Ships.Commands;
using EveUtils.Shared.Modules.Ships.Dtos;
using EveUtils.Shared.Modules.Ships.Events;
using EveUtils.Shared.Modules.Ships.Queries;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.ViewModels;

public partial class MainWindowViewModel : ViewModelBase, IModuleHostDisplay
{
    private readonly IServiceProvider? _services;
    private readonly GamelogClientService? _gamelog;
    private readonly LocalEsiLoginService? _login;
    private readonly ServerPairingService? _pairing;
    private readonly IRemoteBusConnector? _busConnector;
    private readonly SyntheticDpsFeeder? _feeder;
    private readonly ICharacterRegistry? _registry;
    private readonly ICharacterInfoService? _characterInfo;
    private readonly EveUtils.Client.Platform.EveClientPresenceService? _clientPresence;
    private readonly ICharacterPortraitProvider? _portraits;
    private readonly IThemeService? _theme;
    private readonly IDialogService? _dialogs;
    private readonly IEsiAvailabilityState? _availability;
    private readonly IEsiScopeRegistry? _scopeRegistry;
    private readonly ServerFitShareClient? _fitShare;
    private readonly IFitExportActions? _fitExportActions;
    private readonly ServerCouplingService? _coupling;
    private readonly IServerRegistry? _serverRegistry;
    private readonly FleetClient? _fleetClient;
    private readonly Random _random = new();
    private readonly Dictionary<string, DpsViewModel> _trackersByCharacter = new(StringComparer.OrdinalIgnoreCase);
    private readonly GamelogWatcherService? _watcher;
    private readonly HashSet<string> _observedCharacters = new(StringComparer.OrdinalIgnoreCase);
    private FittingsTabViewModel _localFitsTab = null!;
    private bool _outgoing = true;
    private CancellationTokenSource? _feedCts;
    private CancellationTokenSource? _signInCts;
    private readonly DpsRenderDriver? _renderDriver;
    private string _localCharacter = "Pilot-" + (Environment.GetEnvironmentVariable("EVEUTILS_INSTANCE") ?? "Local");

    // ── Collections ──────────────────────────────────────────────────────────────────────────────

    public ObservableCollection<ShipDto> Ships { get; } = [];
    public ObservableCollection<SettingDto> Settings { get; } = [];
    public ObservableCollection<DpsViewModel> DpsTrackers { get; } = [];
    public ObservableCollection<CharacterViewModel> Characters { get; } = [];
    public ObservableCollection<FittingViewModel> Fittings { get; } = [];

    /// <summary>Fittings tabs: the Local tab first, then one per coupled server.</summary>
    public ObservableCollection<FittingsTabViewModel> FittingTabs { get; } = [];

    // ── Observable properties ────────────────────────────────────────────────────────────────────

    [ObservableProperty] private string _newShipName = "Tristan";
    [ObservableProperty] private string _feedButtonText = "START FEED";

    // Transient activity line: pairing progress, couple/decouple results, errors. Empty = idle.
    [ObservableProperty] private string _activityStatus = "";
    [ObservableProperty] private string _fittingsStatus = "";

    // Tranquility server status (ESI /status/, polled every 30 s by EveServerStatusService). Shown right-aligned
    // in the bottom bar; the brush colours the whole indicator green/amber/red/grey by state.
    [ObservableProperty] private string _tranquilityStatus = "TRANQUILITY  ●  …";
    [ObservableProperty] private IBrush _tranquilityBrush = UnknownStatusBrush;

    // Prominent downtime banner (top of the window): shown when EVE is in maintenance/VIP so the user knows
    // why non-essential ESI calls are paused (the EsiGatingHandler withholds them). Empty/hidden when up.
    [ObservableProperty] private bool _isEsiAlert;
    [ObservableProperty] private string _esiAlertMessage = "";

    private static readonly IBrush OnlineStatusBrush = new SolidColorBrush(Color.Parse("#FF6FCF97"));
    private static readonly IBrush VipStatusBrush = new SolidColorBrush(Color.Parse("#FFE0B341"));
    private static readonly IBrush OfflineStatusBrush = new SolidColorBrush(Color.Parse("#FFCC4444"));
    private static readonly IBrush UnknownStatusBrush = new SolidColorBrush(Color.Parse("#FF8A8A8A"));
    [ObservableProperty] private FittingsTabViewModel? _selectedFittingsTab;

    // The character highlighted in the list. Selection only drives the list highlight; per-character
    // settings (scopes, servers, couple/decouple) live in the settings dialog now, and actions ask via a
    // picker — there is no "active character".
    [ObservableProperty] private CharacterViewModel? _selectedCharacter;
    [ObservableProperty] private bool _isSigningIn;

    // ── Module-shell state ─────────────────────────────────────────────────────────────────
    // Two independent axes (mockup): DockMode (docked host vs. floating windows + narrow shell) and the
    // collapsed character column. The derived bools below drive the responsive titlebar/window-control layout.
    private const string DockModeSettingKey = "ui.dock-mode";
    private const string CharsCollapsedSettingKey = "ui.chars-collapsed";

    /// <summary>Floating mode: the host collapses to a narrow shell and modules open as separate windows.</summary>
    [ObservableProperty] private bool _isFloating;

    /// <summary>Collapses the character column to leave just the rail (smallest launcher state), in either mode.</summary>
    [ObservableProperty] private bool _isCharsCollapsed;

    /// <summary>The rail group of the currently-selected host tab (null = home → no rail item highlighted).</summary>
    public string? ActiveModule => SelectedHostTab?.ModuleKey;

    public string DockModeLabel => IsFloating ? "FLOATING" : "DOCKED";
    public string DockToggleLabel => IsFloating ? "DOCK" : "FLOAT";
    public string CharsToggleLabel => IsCharsCollapsed ? "SHOW" : "HIDE";
    public bool ShowHost => !IsFloating;
    public bool ShowChars => !IsCharsCollapsed;
    public bool ShowMaximizeButton => !IsFloating;                              // maximize is pointless on a narrow shell
    public bool ShowHeaderWindowButtons => !IsFloating;                         // docked: min/max/close in the header
    public bool ShowRailWindowButtons => IsFloating;                            // floating: min/close fixed in the rail bottom
    public bool ShowBrandText => !IsFloating;                                   // narrow shell shows only the badge logo
    public bool CenterBrand => IsFloating;                                      // floating: centre the badge logo
    public Avalonia.Layout.HorizontalAlignment BrandAlignment =>
        CenterBrand ? Avalonia.Layout.HorizontalAlignment.Center : Avalonia.Layout.HorizontalAlignment.Left;

    /// <summary>The badge logo is the only brand on the narrow floating shell, so it gets more presence there
    /// (with or without the character column); docked keeps the compact logo beside the brand text.</summary>
    public double BrandLogoHeight => IsFloating ? 38 : 26;
    public double TitleBarHeight => IsFloating ? 56 : 44;

    /// <summary>Tooltip for the compact rail status dot (floating mode, where the wide bottom bar does not fit):
    /// the Tranquility line plus any current activity message.</summary>
    public string RailStatusTooltip =>
        string.IsNullOrWhiteSpace(ActivityStatus) ? TranquilityStatus : $"{TranquilityStatus}\n{ActivityStatus}";

    partial void OnTranquilityStatusChanged(string value) => OnPropertyChanged(nameof(RailStatusTooltip));
    partial void OnActivityStatusChanged(string value) => OnPropertyChanged(nameof(RailStatusTooltip));

    public bool IsFitsActive => ActiveModule == "fits";
    public bool IsFleetActive => ActiveModule == "fleet";
    public bool IsEsiActive => ActiveModule == "esi";
    public bool IsInboxActive => ActiveModule == "inbox";
    public bool IsLogsActive => ActiveModule == "logs";
    public bool IsCompositionsActive => ActiveModule == "compositions";

    partial void OnIsFloatingChanged(bool value)
    {
        OnPropertyChanged(nameof(DockModeLabel));
        OnPropertyChanged(nameof(DockToggleLabel));
        OnPropertyChanged(nameof(ShowHost));
        OnPropertyChanged(nameof(ShowMaximizeButton));
        OnPropertyChanged(nameof(ShowHeaderWindowButtons));
        OnPropertyChanged(nameof(ShowRailWindowButtons));
        OnPropertyChanged(nameof(ShowBrandText));
        OnPropertyChanged(nameof(CenterBrand));
        OnPropertyChanged(nameof(BrandAlignment));
        OnPropertyChanged(nameof(BrandLogoHeight));
        OnPropertyChanged(nameof(TitleBarHeight));
        PersistDockMode(value);

        // Migrate the open module set to the new mode (docked stack ↔ floating windows) — no orphans, open set kept.
        _dialogs?.SwitchMode();
    }

    partial void OnIsCharsCollapsedChanged(bool value)
    {
        OnPropertyChanged(nameof(CharsToggleLabel));
        OnPropertyChanged(nameof(ShowChars));
        OnPropertyChanged(nameof(ShowHeaderWindowButtons));
        OnPropertyChanged(nameof(ShowRailWindowButtons));
        OnPropertyChanged(nameof(CenterBrand));
        OnPropertyChanged(nameof(BrandAlignment));
        PersistSetting(CharsCollapsedSettingKey, value ? "true" : "false");
    }

    // The rail highlight follows the selected host tab's module (null = home → nothing highlighted).
    partial void OnSelectedHostTabChanged(HostTab? value)
    {
        OnPropertyChanged(nameof(ActiveModule));
        OnPropertyChanged(nameof(IsFitsActive));
        OnPropertyChanged(nameof(IsFleetActive));
        OnPropertyChanged(nameof(IsEsiActive));
        OnPropertyChanged(nameof(IsInboxActive));
        OnPropertyChanged(nameof(IsLogsActive));
        OnPropertyChanged(nameof(IsCompositionsActive));
    }

    [RelayCommand] private void ToggleDockMode() => IsFloating = !IsFloating;
    [RelayCommand] private void ToggleChars() => IsCharsCollapsed = !IsCharsCollapsed;

    /// <summary>Rail click: open the module's feature (a docked tab, or a floating window). The rail highlight is
    /// derived from the selected tab, so it lights up only once the module is actually open.</summary>
    [RelayCommand]
    private async Task LaunchModule(string? id)
    {
        switch (id)
        {
            // FITS opens the full fit browser in both modes (consistent): hosted in docked, a window in floating.
            // The home dashboard (live DPS) remains the landing shown at startup and when no tab is open.
            case "fits": await OpenFitBrowser(); break;
            case "fleet": OpenFleets(); break;
            case "compositions": OpenCompositions(); break;
            case "esi": OpenEsiMetrics(); break;
            case "inbox": OpenInbox(); break;
            case "logs": OpenLogs(); break;
            case "settings": await OpenSettings(); break;
            case "about": await OpenAbout(); break;
        }
    }

    private void PersistDockMode(bool floating) => PersistSetting(DockModeSettingKey, floating ? "floating" : "docked");

    // Persists a small UI shell preference off the UI thread. Open modules are intentionally not
    // restored: re-opening live modules (ESI/Fleet/browser) on startup would trigger fetches and clutter — the user
    // re-opens them on demand. Only the lightweight shell prefs (dock mode, collapse) survive a restart.
    private void PersistSetting(string key, string value)
    {
        if (_services is null) return;
        _ = Task.Run(async () =>
        {
            using var scope = _services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send(new SetSettingCommand(key, value));
        });
    }

    // ── Docked module host display (IModuleHostDisplay) ─────────────────────────────────────────────
    // The ModuleHostService owns the open module set + lifecycle and drives this tab collection; in docked mode the
    // host shows a tab per open module (empty = the live-DPS home), in floating mode they are separate windows.

    /// <summary>The open module tabs shown in the docked host (empty = the home landing).</summary>
    public ObservableCollection<HostTab> HostTabs { get; } = [];

    /// <summary>The active host tab (its content fills the host).</summary>
    [ObservableProperty] private HostTab? _selectedHostTab;

    /// <summary>True when the host shows the home landing (no module tabs open).</summary>
    public bool IsHomeShown => HostTabs.Count == 0;

    /// <summary>The remote bus connector, exposed so the character dialog can read per-server state and
    /// subscribe to live state changes while it is open.</summary>
    public IRemoteBusConnector? Bus => _busConnector;

    /// <summary>The message inbox: owns the live message collection + the unread badge. Subscribes to
    /// the bus itself, so it is created once here and shared with the (non-modal) inbox window.</summary>
    public InboxViewModel Inbox { get; }

    /// <summary>The client log window's view-model: reads this client's in-memory error log. Created
    /// once here and shared with the non-modal log window so it keeps updating live while open.</summary>
    public ClientLogViewModel Logs { get; }

    /// <summary>The home dashboard: your own characters' live DPS, your fleets, the latest shared fits and recent
    /// activity. Replaces the old global live-DPS landing that showed every connected client's DPS.</summary>
    public HomeDashboardViewModel Home { get; }

    // ── Constructors ─────────────────────────────────────────────────────────────────────────────

    public MainWindowViewModel()
    {
        Inbox = new InboxViewModel();
        Logs = new ClientLogViewModel();
        Home = new HomeDashboardViewModel();
        SetupLocalFittingsTab();
    }

    public MainWindowViewModel(IServiceProvider services)
    {
        _services = services;
        _gamelog = services.GetRequiredService<GamelogClientService>();
        _login = services.GetRequiredService<LocalEsiLoginService>();
        _pairing = services.GetRequiredService<ServerPairingService>();
        _busConnector = services.GetRequiredService<IRemoteBusConnector>();
        _feeder = services.GetRequiredService<SyntheticDpsFeeder>();
        _watcher = services.GetRequiredService<GamelogWatcherService>();
        _watcher.CharacterObserved += OnGamelogCharacterObserved;
        _registry = services.GetRequiredService<ICharacterRegistry>();
        _dialogs = services.GetRequiredService<IDialogService>();
        _scopeRegistry = services.GetRequiredService<IEsiScopeRegistry>();
        _fitShare = services.GetRequiredService<ServerFitShareClient>();
        _fitExportActions = services.GetRequiredService<IFitExportActions>();
        _coupling = services.GetRequiredService<ServerCouplingService>();
        _serverRegistry = services.GetRequiredService<IServerRegistry>();
        _fleetClient = services.GetRequiredService<FleetClient>();
        Inbox = services.GetRequiredService<InboxViewModel>(); // subscribes to MessageDeliveredEvent on the bus
        Logs = services.GetRequiredService<ClientLogViewModel>(); // subscribes to ILogStore.EntryAdded
        Home = new HomeDashboardViewModel(services, DpsTrackers); // tracks the self DPS subset + loads fleets/fits/stats

        SetupLocalFittingsTab();

        _gamelog.SetCharacter(_localCharacter);

        // Keep my own meters' bounty + location live so a popped-out overlay shows them like a fleet-metrics row.
        // The gamelog raises MetricsChanged on a bounty payout / jump; refresh the self trackers from its snapshot.
        _gamelog.MetricsChanged += () =>
            Avalonia.Threading.Dispatcher.UIThread.Post(RefreshSelfTrackerMetrics);

        var bus = services.GetRequiredService<IEventBus>();
        bus.Subscribe<CombatLoggedEvent>(OnCombat);
        bus.Subscribe<ShipAddedEvent>(OnShipAdded);
        bus.Subscribe<FitSharedEvent>(OnFitShared);
        // fleet invites now arrive as messages in the Inbox (single channel) — no separate popup.
        bus.Subscribe<FleetInviteRespondedEvent>(OnFleetInviteResponded); // inviter sees the outcome

        _registry.RegistryChanged += () =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = RefreshCharactersAsync());

        // Live per-server bus state → the matching character's server link indicator.
        if (_busConnector is not null)
            _busConnector.StateChanged += (address, state) =>
                Avalonia.Threading.Dispatcher.UIThread.Post(() => ApplyServerConnectionState(address, state));

        // Live Tranquility status → the bottom-bar indicator. Seed from the current snapshot (the poller may have
        // already run before this VM existed) and follow further changes.
        _availability = services.GetRequiredService<IEsiAvailabilityState>();
        var serverStatus = services.GetRequiredService<EveServerStatusService>();
        _ApplyServerStatus(serverStatus.Current);
        serverStatus.Changed += snapshot =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => _ApplyServerStatus(snapshot));

        // Live local-client presence → the green dot per character row. Seed from the current sweep (the poller
        // may have run before this VM existed) and follow changes; rebuilt rows re-seed in RefreshCharactersAsync.
        _clientPresence = services.GetRequiredService<EveUtils.Client.Platform.EveClientPresenceService>();
        _clientPresence.Changed += evidence =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => _ApplyClientPresence(evidence));

        // Live public affiliation (corp/alliance) per character, kept fresh by CharacterInfoRefreshService on the
        // metered ESI pipeline. Seed each rebuilt row from the cache (RefreshCharactersAsync) and follow changes.
        _portraits = services.GetRequiredService<ICharacterPortraitProvider>();
        _theme = services.GetRequiredService<IThemeService>();
        _characterInfo = services.GetRequiredService<ICharacterInfoService>();
        _characterInfo.AffiliationChanged += (characterId, info) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => _ApplyAffiliation(characterId, info));

        // Live implant badge: a freshly added character imports its implants in the background after its row
        // is already built, so follow the importer's change event to refresh the badge without a re-auth/restart.
        var implantImporter = services.GetService<IEsiImplantImporter>();
        if (implantImporter is not null)
            implantImporter.ImplantsChanged += (characterId, typeIds) =>
                Avalonia.Threading.Dispatcher.UIThread.Post(() => _ApplyImplants(characterId, typeIds));

        // Smooth, demo-parity DPS graphs: every tracker (own + fleet) renders through the one shared
        // ~30fps DpsRenderDriver, so the curve scrolls + decays continuously and all graphs share one render path.
        _renderDriver = services.GetRequiredService<DpsRenderDriver>();

        _ = LoadAsync();
    }

    // Marks every character row whose EVE client is currently running on this machine (matched on the window-title
    // name OR the launcher's character id — see EveClientEvidence for why both exist).
    private void _ApplyClientPresence(EveUtils.Client.Platform.EveClientEvidence evidence)
    {
        foreach (var vm in Characters)
            vm.HasActiveClient = evidence.Matches(vm.Name, vm.CharacterId);
    }

    // Writes a resolved affiliation onto the matching character row (the list is rebuilt independently, so a
    // change that arrives between rebuilds still lands on the current row).
    private void _ApplyAffiliation(int characterId, CharacterPublicInfo? info)
    {
        var vm = Characters.FirstOrDefault(c => c.CharacterId == characterId);
        if (vm is not null)
            vm.Affiliation = info?.AffiliationLabel ?? "—";
    }

    // Writes the resolved implants onto the matching character row the moment the background import finishes, so the
    // overview badge appears without waiting for the next list rebuild.
    private void _ApplyImplants(int characterId, IReadOnlyList<int> typeIds)
    {
        var vm = Characters.FirstOrDefault(c => c.CharacterId == characterId);
        if (vm is not null)
            vm.SetImplants(typeIds.Select(FitNames().TypeName).ToList());
    }

    // Renders a Tranquility snapshot into the bottom-bar text + colour and the top downtime banner.
    private void _ApplyServerStatus(EveServerStatusSnapshot status)
    {
        (TranquilityStatus, TranquilityBrush) = status.State switch
        {
            EveServerState.Online => ($"TRANQUILITY  ●  {status.Players?.ToString("N0", CultureInfo.InvariantCulture)} online", OnlineStatusBrush),
            EveServerState.Vip => ("TRANQUILITY  ●  VIP only", VipStatusBrush),
            EveServerState.Offline => ("TRANQUILITY  ●  offline", OfflineStatusBrush),
            _ => ("TRANQUILITY  ●  …", UnknownStatusBrush)
        };

        // Banner = "why are calls paused": consistent with the gate, so it also shows on a timeout/unreachable
        // downtime (Unknown), not just a confirmed 5xx. Default usable=true → no banner before the first poll.
        (IsEsiAlert, EsiAlertMessage) = EsiDowntimeBanner.For(_availability?.IsUsable ?? true, status.State);
    }

    private void SetupLocalFittingsTab()
    {
        _localFitsTab = new FittingsTabViewModel("Local", Fittings);
        FittingTabs.Add(_localFitsTab);
        SelectedFittingsTab = _localFitsTab;
        HostTabs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsHomeShown));   // home shows when no tabs
    }

    /// <summary>Opens the Fleets window — non-modal so its live member graphs run alongside the main window.</summary>
    [RelayCommand]
    private void OpenFleets()
    {
        if (_services is null || _dialogs is null)
            return;
        _dialogs.ShowFleets(new FleetsViewModel(_services));
    }

    /// <summary>Opens the Fleet Compositions library — the reusable-doctrine module, hosted like the
    /// other feature modules (docked tab or floating window).</summary>
    [RelayCommand]
    private void OpenCompositions()
    {
        if (_services is null || _dialogs is null)
            return;
        _dialogs.ShowCompositions(new CompositionsViewModel(_services));
    }

    /// <summary>Opens the message inbox — non-modal so deliveries keep arriving while it is open.</summary>
    [RelayCommand]
    private void OpenInbox()
    {
        if (_dialogs is null)
            return;
        _dialogs.ShowInbox(Inbox);
    }

    /// <summary>Opens the client log window — non-modal so new errors keep arriving while it is open.</summary>
    [RelayCommand]
    private void OpenLogs()
    {
        if (_dialogs is null)
            return;
        _dialogs.ShowLogs(Logs);
    }

    /// <summary>Opens the ESI-metrics window — non-modal; a fresh view-model per open so its live poll
    /// timer only runs while the window is visible.</summary>
    [RelayCommand]
    private void OpenEsiMetrics()
    {
        if (_dialogs is null || _services is null)
            return;
        _dialogs.ShowEsiMetrics(new EsiMetricsViewModel(
            _services.GetRequiredService<IEsiRateLimitMonitor>(),
            _services.GetRequiredService<ICharacterRegistry>(),
            _services.GetRequiredService<ICharacterPortraitProvider>()));
    }

    /// <summary>Open the FITS fit-browser window: the Local library plus a tab per coupled server, each a
    /// searchable, paged grid with a slot-detail panel. Local rows are built up front; server tabs load lazily.</summary>
    [RelayCommand]
    private async Task OpenFitBrowser()
    {
        if (_services is null || _dialogs is null) return;

        var names = FitNames();
        // Edit/delete a local fit's metadata (fit-metadata): the dialog + repo + reload live here where the services and
        // the Local-tab refresh are in scope; the rows reach back through these callbacks. localTab is assigned below —
        // the callbacks only fire on a later user action, by which point it is set.
        FitBrowserTabViewModel localTab = null!;
        async Task ReloadLocalAsync() => localTab.SetRows(await BuildLocalFitRowsAsync(names, EditFitMetadataAsync, DeleteFitAsync));
        async Task EditFitMetadataAsync(int localFitId) => await EditLocalFitMetadataAsync(localFitId, ReloadLocalAsync);
        async Task DeleteFitAsync(int localFitId) => await DeleteLocalFitByIdAsync(localFitId, ReloadLocalAsync);
        // Hand the Local-tab refresh to the detail window's in-place metadata edit (OpenFitDetailAsync, opened from here).
        _reloadLocalFits = ReloadLocalAsync;

        localTab = new FitBrowserTabViewModel(
            "Local library", await BuildLocalFitRowsAsync(names, EditFitMetadataAsync, DeleteFitAsync), names);
        var tabs = new List<FitBrowserTabViewModel> { localTab };

        var sessionStore = _services.GetRequiredService<IClientSessionStore>();
        foreach (var addr in await sessionStore.ListServersAsync())
        {
            var display = _serverRegistry is null ? addr : await _serverRegistry.DisplayNameAsync(addr);
            tabs.Add(new FitBrowserTabViewModel(display, addr, LoadServerFitBrowserTabAsync, names));
        }
        // After a single-fit import (EFT/DNA or ESF — not the ESI multi-select), pop the detail open unless the user
        // turned it off in Settings.
        async Task ImportThenMaybeOpenAsync(Func<Task<string?>> import)
        {
            var importedName = await import();
            await ReloadLocalAsync();
            if (importedName is not null && await ShouldOpenDetailAfterImportAsync()
                && localTab.FindByName(importedName) is { } row)
                await OpenFitDetailAsync(row);
        }
        _dialogs.ShowFitBrowser(new FitBrowserViewModel(
            tabs, OpenFitDetailAsync,
            importEsi: async () => { await ImportFittings(); await ReloadLocalAsync(); },
            importText: () => ImportThenMaybeOpenAsync(ImportFitText),
            importEsfLink: () => ImportThenMaybeOpenAsync(ImportFitEsfLink)));
    }

    // Set when the fit-browser builds its Local tab; lets the detail window's in-place metadata edit refresh that tab.
    private Func<Task>? _reloadLocalFits;

    private const string SkillModeSettingKey = "fit-detail.skill-mode";   // remembered selector mode ("all:5"/"char:42")
    private const string ImplantModeSettingKey = "fit-detail.implant-mode";   // remembered implant source ("fit"/"char:42")
    private const string OpenDetailAfterImportSettingKey = "fittings.open-detail-after-import";   // default on

    /// <summary>Open the radial fit-detail window for a fit: compute its stats via the Dogma engine, then show
    /// the fitting wheel + stats panels. Stats are null when the SDE has not been imported — the window then notes it.</summary>
    private async Task OpenFitDetailAsync(FitRowViewModel row)
    {
        if (_services is null || _dialogs is null) return;
        var fit = row.Fit;
        // fit-metadata: a local fit carries the user's notes + tags (server-shared rows don't) — shown in the header.
        var metadata = row.LocalFitId is { } metaId
            ? await _services.GetRequiredService<IFittingRepository>().FindByIdAsync(metaId)
            : null;
        var characters = Characters.Select(c => (c.CharacterId, c.Name)).ToList();
        var settings = _services.GetService<ISettingRepository>();
        var rememberedSkillMode = settings is null
            ? null
            : (await settings.ListAsync()).FirstOrDefault(s => s.Key == SkillModeSettingKey)?.Value;
        Func<string, Task>? onSkillModeChanged = settings is null
            ? null
            : value => settings.UpsertAsync(SkillModeSettingKey, value);
        var rememberedImplantMode = settings is null
            ? null
            : (await settings.ListAsync()).FirstOrDefault(s => s.Key == ImplantModeSettingKey)?.Value;
        Func<string, Task>? onImplantModeChanged = settings is null
            ? null
            : value => settings.UpsertAsync(ImplantModeSettingKey, value);
        // In-place metadata edit from the detail header — local fits only; reuses the browser's dialog+repo flow and
        // refreshes the Local tab through the stored reload.
        Func<int, Task<FitMetadataDraft?>>? onEditMetadata = row.LocalFitId is null
            ? null
            : id => EditLocalFitMetadataAsync(id, _reloadLocalFits ?? (() => Task.CompletedTask));
        var viewModel = new FitDetailWindowViewModel(fit, FitNames(),
            _services.GetService<IFitStatsProvider>(),
            _services.GetService<ISdeAccessor>(),
            _services.GetService<IDogmaDataAccessor>(),
            _services.GetService<ITypeImageProvider>(),
            _services.GetService<IMarketPriceRepository>(),
            ShowTypeInfoAsync,
            characters,
            _services.GetService<IEsiSkillImporter>(),
            _services.GetService<ICharacterSkillRepository>(),
            rememberedSkillMode,
            onSkillModeChanged,
            _services.GetService<IEsiImplantImporter>(),
            _services.GetService<ICharacterImplantRepository>(),
            rememberedImplantMode,
            onImplantModeChanged,
            _fitExportActions,            // the shared export seam
            row.LocalFitId,               // local DB id (null for a not-yet-downloaded server fit) → export disabled
            BuildPickOptions,             // character-picker source for push/share
            metadata?.Description,        // fit-metadata: user notes + tags, shown read-only in the header
            metadata?.Tags,
            _services.GetService<ICharacterAttributesRepository>(),   // SP/time rate for the Skills Required panel
            _services.GetService<IToastService>(),                    // toast on a refused module activation (cloak conflict)
            onEditMetadata);                                          // in-place edit of the fit's name/notes/tags (local fits)
        await viewModel.InitializeAsync();
        _dialogs.ShowFitDetail(viewModel);
        _ = viewModel.LoadImagesAsync();   // opt-in CCP images pop in after the window shows
    }

    /// <summary>Opens a "Show Info" card for a module/charge type: name + group/category from the SDE, the
    /// estimated market value and the icon.</summary>
    private async Task ShowTypeInfoAsync(int typeId)
    {
        if (_services is null || _dialogs is null) return;

        var sde = _services.GetService<ISdeAccessor>();
        var name = $"type {typeId}";
        var category = "";
        if (sde is not null)
        {
            if (sde.TryGetTypeName(typeId, out var resolved)) name = resolved;
            var type = sde.GetType(typeId);
            var group = type is null ? null : sde.GetGroup(type.GroupId);
            var cat = group is null ? null : sde.GetCategory(group.CategoryId);
            category = string.Join(" · ", new[] { group?.Name, cat?.Name }.Where(s => !string.IsNullOrWhiteSpace(s)));
        }

        double? averagePrice = null;
        if (_services.GetService<IMarketPriceRepository>() is { } prices)
        {
            var map = await prices.GetAveragePricesAsync([typeId]);
            if (map.TryGetValue(typeId, out var price)) averagePrice = price;
        }

        Avalonia.Media.Imaging.Bitmap? image = null;
        if (_services.GetService<ITypeImageProvider>() is { } images && await images.AreImagesEnabledAsync())
            image = await images.GetImageAsync(typeId, TypeImageKind.Icon, 64);

        _dialogs.ShowTypeInfo(new TypeInfoWindowViewModel(typeId, name, category, averagePrice, image));
    }

    /// <summary>SDE-backed type-name resolver for the browser, or a fallback (<c>type {id}</c>) when the SDE store
    /// has not been imported yet.</summary>
    private ISdeNameResolver FitNames() => FitNameResolverFactory.For(_services);

    private async Task<List<FitRowViewModel>> BuildLocalFitRowsAsync(
        ISdeNameResolver names, Func<int, Task>? onEditMetadata = null, Func<int, Task>? onDelete = null)
    {
        using var scope = _services!.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
        var list = await dispatcher.Query(new GetFittingsQuery());

        var rows = new List<FitRowViewModel>();
        foreach (var f in list)
        {
            var fit = TryParseFit(f.RawJson);
            if (fit is null) continue;
            var ownerName = Characters.FirstOrDefault(c => c.OwnerId == f.OwnerId)?.Name ?? f.OwnerId;
            // local DB id drives export; hull/module icons via the type-image provider; the per-row
            // export dropdown reaches the shared seam with the picker + status sink; edit/delete metadata
            // (fit-metadata) reach back to the browser composition through the callbacks
            var row = new FitRowViewModel(fit, ownerName, names, f.Id, _services!.GetService<ITypeImageProvider>(),
                _fitExportActions, BuildPickOptions, status => FittingsStatus = status,
                _services!.GetService<IMarketPriceRepository>(), onEditMetadata, onDelete, tags: f.Tags);
            _ = row.LoadHullImageAsync();   // opt-in CCP render: no-op + null when images are off
            _ = row.LoadPriceAsync();       // estimated fit value from the cached ESI prices, on demand
            rows.Add(row);
        }
        return rows;
    }

    private async Task LoadServerFitBrowserTabAsync(FitBrowserTabViewModel tab)
    {
        if (_fitShare is null || tab.ServerAddress is null) return;
        if (_busConnector?.StateFor(tab.ServerAddress) != ServerConnectionState.Connected)
        {
            tab.Status = "Not connected — couple a character to this server first.";
            return;
        }

        tab.Status = "Fetching server fits…";
        var (ok, message, serverFits) = await _fitShare.GetSharedFitsAsync(tab.ServerAddress);
        if (!ok) { tab.Status = $"Server fits unavailable: {message}"; return; }

        var names = FitNames();
        var rows = new List<FitRowViewModel>();
        foreach (var sf in serverFits)
        {
            var fit = TryParseFit(sf.RawJson);
            // server fits have no local id (null) → export disabled; the sharer is the uploader, icons via the provider
            if (fit is null) continue;
            var row = new FitRowViewModel(fit, sf.SharedByCharacterName, names, null, _services!.GetService<ITypeImageProvider>(),
                prices: _services!.GetService<IMarketPriceRepository>());
            _ = row.LoadHullImageAsync();   // opt-in CCP render: no-op + null when images are off
            _ = row.LoadPriceAsync();       // estimated fit value from the cached ESI prices, on demand
            rows.Add(row);
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            tab.SetRows(rows);
            tab.Status = rows.Count == 0 ? "No fits shared on this server yet." : $"{rows.Count} shared fit(s).";
        });
    }

    private static EsiFitting? TryParseFit(string rawJson)
    {
        try { return JsonSerializer.Deserialize<EsiFitting>(rawJson); }
        catch { return null; }
    }

    // The inviter's side: the invitee's accept/deny comes back as a targeted event.
    private void OnFleetInviteResponded(FleetInviteRespondedEvent integrationEvent) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            ActivityStatus = integrationEvent.Data.Accepted
                ? "A character accepted your fleet invite."
                : "A character declined your fleet invite.");

    // Update the live state of every server link pointing at this address. A character can be
    // coupled to several servers, so we match per-link rather than painting one global state.
    private void ApplyServerConnectionState(string address, ServerConnectionState state)
    {
        foreach (var c in Characters)
            foreach (var link in c.ServerLinks)
                if (string.Equals(link.Address, address, StringComparison.OrdinalIgnoreCase))
                    link.State = state;
    }

    // Lazy-load a server tab the first time it is shown.
    partial void OnSelectedFittingsTabChanged(FittingsTabViewModel? value)
    {
        if (value is { IsLocal: false, IsLoaded: false })
            _ = value.EnsureLoadedAsync();
    }

    /// <summary>
    /// On startup, check each character's ESI token: refresh if expiring, flag "re-auth needed" if the
    /// refresh fails. Shows one summary message if any character needs re-authentication.
    /// </summary>
    private async Task CheckTokensOnStartupAsync()
    {
        if (_services is null) return;
        var refresher = _services.GetRequiredService<ClientTokenRefreshService>();

        var needReauth = new List<string>();
        foreach (var c in Characters)
        {
            var status = await refresher.EnsureValidAsync(c.CharacterId);
            if (status is TokenStatus.NeedsReauth)
            {
                c.NeedsReauth = true;
                needReauth.Add(c.Name);
            }
        }

        if (needReauth.Count > 0 && _dialogs is not null)
        {
            await _dialogs.ShowMessageAsync(
                "Re-authentication needed",
                $"The ESI session expired for: {string.Join(", ", needReauth)}.\n\n" +
                "Sign in again for those characters to restore ESI access.");
        }
    }

    // ── Character management ──────────────────────────────────────────────────────────────────────

    [RelayCommand]
    private Task AddCharacter() => SignInWithScopeDialogAsync(isNew: true);

    /// <summary>
    /// App settings dialog: configure the gamelog directory. Persists the path via the
    /// Settings module and re-baselines the live gamelog watcher there.
    /// </summary>
    [RelayCommand]
    private async Task OpenSettings()
    {
        if (_dialogs is null || _services is null) return;

        string current;
        MetricShareSnapshot shares;
        bool loadImages;
        bool openDetailAfterImport;
        Notifications.ToastPosition toastPosition;
        bool localApiEnabled;
        int localApiPort;
        using (var scope = _services.CreateScope())
        {
            var settings = await scope.ServiceProvider.GetRequiredService<IDispatcher>().Query(new GetSettingsQuery());
            current = settings.FirstOrDefault(s => s.Key == GamelogWatcherService.GamelogDirectorySettingKey)?.Value ?? "";
            // The global per-metric share defaults, read through the same gate the publisher uses.
            shares = new MetricShareSnapshot(settings.ToDictionary(s => s.Key, s => s.Value, StringComparer.Ordinal));
            loadImages = settings.FirstOrDefault(s => s.Key == TypeImageProvider.EnabledSettingKey)?.Value != "false"; // default on
            openDetailAfterImport = settings.FirstOrDefault(s => s.Key == OpenDetailAfterImportSettingKey)?.Value != "false"; // default on
            toastPosition = Enum.TryParse<Notifications.ToastPosition>(
                settings.FirstOrDefault(s => s.Key == Notifications.ToastService.PositionSettingKey)?.Value, ignoreCase: true, out var tp)
                ? tp : Notifications.ToastPosition.TopRight;
            localApiEnabled = settings.FirstOrDefault(s => s.Key == LocalApi.LocalApiServer.EnabledSettingKey)?.Value == "true"; // default off
            localApiPort = int.TryParse(settings.FirstOrDefault(s => s.Key == LocalApi.LocalApiServer.PortSettingKey)?.Value, out var lp)
                ? lp : LocalApi.LocalApiServer.DefaultPort;
        }

        var localApi = _services.GetService<LocalApi.ILocalApiServer>();
        var localApiStatusLabel = localApi is not null ? _LocalApiStatusLabel(localApi.Status) : "";
        _dialogs.ShowSettings(
            current, GameLogLocations.Default(),
            shares.IsShared(MetricKind.Location), shares.IsShared(MetricKind.Bounty), shares.IsShared(MetricKind.Dps),
            loadImages, _theme?.Current ?? FactionTheme.Gallente, SdeVersionLabel(), ApplySettingsAsync, openDetailAfterImport, toastPosition,
            localApiEnabled, localApiPort, localApiStatusLabel, localApi);
    }

    /// <summary>Opens the About dialog: app identity + version, creator credits with portraits,
    /// inspiration links, the AGPLv3 license and the mandatory CCP attribution.</summary>
    [RelayCommand]
    private async Task OpenAbout()
    {
        if (_dialogs is null) return;
        var characterInfo = _services?.GetService<ICharacterInfoService>();
        await _dialogs.ShowAboutAsync(new AboutViewModel(_portraits, characterInfo));
    }

    /// <summary>Persist + apply the settings chosen in the settings module (invoked on Save; Cancel/close never calls
    /// this). Persists each value, re-tints the theme, syncs the toast position + local API host, restarts the gamelog
    /// watcher and, if requested, runs a forced SDE re-import.</summary>
    private async Task ApplySettingsAsync(SettingsResult result)
    {
        if (_services is null) return;

        var localApi = _services.GetService<LocalApi.ILocalApiServer>();

        // Apply + persist the chosen faction theme: re-tints the whole surface live.
        _theme?.Apply(result.Faction);

        using (var scope = _services.CreateScope())
        {
            var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
            if (!string.IsNullOrWhiteSpace(result.GamelogDirectory))
                await dispatcher.Send(new SetSettingCommand(
                    GamelogWatcherService.GamelogDirectorySettingKey, result.GamelogDirectory));
            await dispatcher.Send(new SetSettingCommand(
                MetricShareSnapshot.KeyFor(MetricKind.Location), result.ShareLocation ? "true" : "false"));
            await dispatcher.Send(new SetSettingCommand(
                MetricShareSnapshot.KeyFor(MetricKind.Bounty), result.ShareBounty ? "true" : "false"));
            await dispatcher.Send(new SetSettingCommand(
                MetricShareSnapshot.CombatShareKey, result.ShareCombat ? "true" : "false"));
            await dispatcher.Send(new SetSettingCommand(
                TypeImageProvider.EnabledSettingKey, result.LoadTypeImages ? "true" : "false"));
            await dispatcher.Send(new SetSettingCommand(
                OpenDetailAfterImportSettingKey, result.OpenFitDetailAfterImport ? "true" : "false"));
            await dispatcher.Send(new SetSettingCommand(
                Notifications.ToastService.PositionSettingKey, result.ToastPosition.ToString()));
            await dispatcher.Send(new SetSettingCommand(
                LocalApi.LocalApiServer.EnabledSettingKey, result.EnableLocalApi ? "true" : "false"));
            await dispatcher.Send(new SetSettingCommand(
                LocalApi.LocalApiServer.PortSettingKey, result.LocalApiPort.ToString()));
        }

        // Apply the toast position live so the next toast uses it without a restart.
        if (_services.GetService<Notifications.ToastService>() is { } toastService)
            toastService.Position = result.ToastPosition;

        // (Re)start or stop the local API host live so the toggle takes effect without a restart. The dialog may
        // already have started/stopped it via its Start/Stop button; re-applying the saved state is idempotent.
        if (localApi is not null)
            await localApi.ApplyAsync(result.EnableLocalApi, result.LocalApiPort);

        if (!string.IsNullOrWhiteSpace(result.GamelogDirectory) && _watcher is not null)
            await _watcher.RestartAsync();
        ActivityStatus = string.IsNullOrWhiteSpace(result.GamelogDirectory)
            ? "Settings saved."
            : $"Gamelog directory set: {result.GamelogDirectory}";

        // Surface a local-API problem (e.g. the chosen port was taken) over the generic confirmation so it is visible.
        if (localApi is { Status.Status: LocalApi.LocalApiStatus.PortInUse or LocalApi.LocalApiStatus.Error })
            ActivityStatus = localApi.Status.Message ?? "Local API could not start.";

        // Fallback/debug: force a fresh SDE download + rebuild behind the progress popup.
        if (result.ReimportSde)
        {
            await RunSdeImportPopupAsync();
            ActivityStatus = $"SDE data: {SdeVersionLabel()}";
        }
    }

    /// <summary>A short human label for the local widget API host state, shown in the Settings dialog.</summary>
    private static string _LocalApiStatusLabel(LocalApi.LocalApiStatusSnapshot status) => status.Status switch
    {
        LocalApi.LocalApiStatus.Running => $"Running on {status.Url}",
        LocalApi.LocalApiStatus.PortInUse => status.Message ?? $"Port {status.Port} is in use",
        LocalApi.LocalApiStatus.Error => status.Message ?? "Failed to start",
        _ => "Stopped"
    };

    /// <summary>
    /// A character whose gamelog the watcher just detected. If it isn't an ESI-registered character it is
    /// surfaced as a local-only row so its DPS is visible; merged in by <see cref="RefreshCharactersAsync"/>.
    /// </summary>
    private void OnGamelogCharacterObserved(string characterName) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (!string.IsNullOrWhiteSpace(characterName) && _observedCharacters.Add(characterName))
                _ = RefreshCharactersAsync();
        });

    /// <summary>Open the metrics window for a character: live DPS/bounty/location/enemies; other local
    /// toons can be ticked in there too. Pre-selects the clicked character.</summary>
    [RelayCommand]
    private void OpenMetrics(CharacterViewModel? character)
    {
        if (_dialogs is null || _services is null) return;
        var characters = Characters.Select(c => (c.Name, c.CharacterId)).ToList();
        _dialogs.ShowMetrics(new MetricsWindowViewModel(_services, characters, character?.Name));
    }

    /// <summary>Pop a tracker's live DPS into a borderless, pinnable overlay.</summary>
    [RelayCommand]
    private void OpenDpsOverlay(DpsViewModel? tracker)
    {
        if (tracker is not null)
            _dialogs?.ShowDpsOverlay(tracker);
    }

    /// <summary>Pop the DPS overlay straight from the character list. Resolves the tracker by character
    /// name — the same key the gamelog DPS stream uses (<c>RouteSample</c>) — so the overlay binds to the exact
    /// instance that real DPS data flows into and updates live. Created on first use if no sample has arrived.</summary>
    [RelayCommand]
    private void OpenCharacterDpsOverlay(CharacterViewModel? character)
    {
        if (character is not null)
            _dialogs?.ShowDpsOverlay(GetOrCreateTracker(character.Name));
    }

    /// <summary>Open the per-character settings dialog: ESI scopes, coupled servers, couple/decouple.</summary>
    [RelayCommand]
    private async Task OpenCharacterSettings(CharacterViewModel? character)
    {
        if (character is null || _dialogs is null) return;
        var vm = new CharacterDialogViewModel(this, character);
        await vm.InitializeAsync();
        await _dialogs.ShowCharacterAsync(vm); // modal; the window disposes the vm on close
        await RefreshCharactersAsync();        // reflect any scope/coupling changes in the list badges
    }

    /// <summary>
    /// Re-authenticate a character through the same scope-selection popup shown at sign-in, called from its
    /// settings dialog. The character's currently granted scopes are pre-ticked, so the user can add or drop ESI scopes.
    /// The popup is built from the scope registry, so it lists every scope the modules declare and scales as new
    /// scopes are added — replacing the former per-scope "+ ADD" buttons. Re-uses the SSO with the chosen set.
    /// </summary>
    public async Task ReAuthenticateAsync(int characterId)
    {
        if (_login is null || _dialogs is null || _scopeRegistry is null || _registry is null) return;

        var granted = (await _registry.GetAllAsync())
            .FirstOrDefault(c => c.EsiCharacterId == characterId)?.GrantedScopes ?? [];
        var available = _scopeRegistry.GetRequirements(EsiScopeTarget.Client);
        var selected = await _dialogs.SelectScopesAsync(available, granted);
        if (selected is null)
        {
            ActivityStatus = "Re-authentication cancelled.";
            return;
        }

        _signInCts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        IsSigningIn = true;
        try
        {
            ActivityStatus = "Re-authenticating… (cancel to abort)";
            var identity = await _login.SignInAsync(selected, _signInCts.Token);
            ActivityStatus = $"Re-authenticated: {identity.CharacterName}";
            await RefreshCharactersAsync();
        }
        catch (OperationCanceledException)
        {
            ActivityStatus = "Re-authentication cancelled.";
        }
        catch (Exception ex)
        {
            ActivityStatus = $"Re-authentication failed: {ex.Message}";
        }
        finally
        {
            IsSigningIn = false;
            _signInCts?.Dispose();
            _signInCts = null;
        }
    }

    /// <summary>
    /// Builds character-picker options for an action that needs the given ESI scope.
    /// Characters missing the scope are shown but disabled, with the reason in the detail line.
    /// </summary>
    private IReadOnlyList<CharacterPickOption> BuildPickOptions(string requiredScope)
    {
        return Characters.Select(c =>
        {
            var has = requiredScope == FittingsScopeCatalog.ReadFittings ? c.HasReadFittings
                    : requiredScope == FittingsScopeCatalog.WriteFittings ? c.HasWriteFittings
                    : true;
            var local = c.IsLocal ? "🏠 local" : "no local token";
            var detail = has ? local : $"{local} · missing {requiredScope}";
            return new CharacterPickOption(c.CharacterId, c.Name, detail, Enabled: has && c.IsLocal);
        }).ToList();
    }

    public async Task RefreshCharactersAsync()
    {
        if (_registry is null) return;

        var previousId = SelectedCharacter?.CharacterId;
        var all = await _registry.GetAllAsync();
        var tokenStore = _services?.GetRequiredService<IPerCharacterTokenStore>();

        // Build the whole list off to the side (all awaits happen here), then swap it into the bound
        // collection in one synchronous block. Two refreshes can run at once (the explicit call after
        // sign-in + the RegistryChanged event); if Clear/Add straddled an await they would interleave
        // and append after each other's Clear, duplicating every character. The atomic swap below has no
        // await between Clear and Add, so the last writer always lands a single, correct list. A character
        // is unique → also dedupe by id.
        var implantRepository = _services?.GetService<ICharacterImplantRepository>();
        var typeNames = FitNames();

        var built = new List<CharacterViewModel>();
        foreach (var c in all) // the registry returns the user-defined order (drag-to-reorder, persisted)
        {
            var charId = c.EsiCharacterId ?? 0;
            if (built.Any(b => b.CharacterId == charId)) continue; // never list a character twice

            var hasLocalToken = tokenStore is not null && await tokenStore.LoadAsync(charId) is not null;
            var vm = new CharacterViewModel(c) { IsLocal = hasLocalToken }; // Mode A: local ESI token present
            vm.Affiliation = _characterInfo?.GetCached(charId)?.AffiliationLabel ?? "—"; // seed from the last resolved value

            // surface the character's plugged-in implants in the overview (badge + tooltip), from the cached set.
            if (implantRepository is not null && charId > 0)
                vm.SetImplants((await implantRepository.GetTypeIdsAsync(charId)).Select(typeNames.TypeName).ToList());

            // One server link per coupled server — drives the cloud-synced N badge on the list row. The full
            // rows (with gear/decouple) live in the character settings dialog, which builds its own links.
            foreach (var link in await BuildServerLinksAsync(charId, DecoupleAsync, onViewTrust: null))
                vm.ServerLinks.Add(link);

            built.Add(vm);
        }

        // Local-only characters observed from gamelogs that aren't ESI-registered: show them so their
        // DPS is visible, but with no ESI link and no server-couple. Deduped by name (their id is 0).
        foreach (var name in _observedCharacters.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            if (built.All(b => !string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase)))
                built.Add(new CharacterViewModel(new Character(name)));

        // Atomic swap — no awaits between Clear and the adds, so concurrent refreshes can't duplicate.
        Characters.Clear();
        foreach (var vm in built)
            Characters.Add(vm);

        // Keep the previously selected character focused, else select the first (master-detail).
        SelectedCharacter = Characters.FirstOrDefault(c => c.CharacterId == previousId)
                            ?? Characters.FirstOrDefault();

        // Rebuilt rows start without the presence dot — re-seed from the latest sweep.
        if (_clientPresence is not null)
            _ApplyClientPresence(_clientPresence.Current);

        _ = LoadCharacterPortraitsAsync(); // hex ESI portraits, best-effort
    }

    /// <summary>Drag-to-reorder a character row: move the dragged character to the dropped-on character's position and
    /// persist the new order so it survives a restart and flows everywhere the list is read (metrics, pickers). Only
    /// ESI-registered characters (id > 0) are reorderable; local-only gamelog rows (id 0) stay appended at the end.</summary>
    public async Task ReorderCharacterAsync(int draggedCharacterId, int targetCharacterId)
    {
        if (_registry is null || draggedCharacterId <= 0 || targetCharacterId <= 0 || draggedCharacterId == targetCharacterId)
            return;

        var dragged = Characters.FirstOrDefault(c => c.CharacterId == draggedCharacterId);
        var target = Characters.FirstOrDefault(c => c.CharacterId == targetCharacterId);
        if (dragged is null || target is null)
            return;

        var from = Characters.IndexOf(dragged);
        var to = Characters.IndexOf(target);
        if (from < 0 || to < 0 || from == to)
            return;

        Characters.Move(from, to); // immediate visual feedback; no rebuild needed
        SelectedCharacter = dragged;

        var order = Characters.Where(c => c.CharacterId > 0).Select(c => c.CharacterId).ToList();
        await _registry.ReorderAsync(order);
    }

    /// <summary>Loads each ESI character's portrait render into its hex. Best-effort: offline/disabled or a
    /// local-only character (id 0) keeps the glyph fallback. Runs on the UI thread (called from the refresh).</summary>
    private async Task LoadCharacterPortraitsAsync()
    {
        if (_portraits is null) return;
        foreach (var c in Characters.ToList())
        {
            if (c.CharacterId <= 0 || c.Portrait is not null) continue;
            var bitmap = await _portraits.GetPortraitAsync(c.CharacterId, 128);
            if (bitmap is not null) c.Portrait = bitmap;
        }
    }

    /// <summary>
    /// Builds the per-server links for a character: one <see cref="ServerLinkViewModel"/> per
    /// coupled server, with its display name and current live bus state. Shared by the character list
    /// (badge only) and the character settings dialog (full rows with gear/decouple wired via the callbacks).
    /// </summary>
    public async Task<List<ServerLinkViewModel>> BuildServerLinksAsync(
        int characterId, Func<ServerLinkViewModel, Task> onDecouple, Func<ServerLinkViewModel, Task>? onViewTrust)
    {
        var links = new List<ServerLinkViewModel>();
        if (_services is null) return links;

        var sessionStore = _services.GetRequiredService<IClientSessionStore>();
        foreach (var addr in await sessionStore.ListServersForCharacterAsync(characterId))
        {
            var display = _serverRegistry is null ? addr : await _serverRegistry.DisplayNameAsync(addr);
            var state = _busConnector?.StateFor(addr) ?? ServerConnectionState.Disconnected;
            links.Add(new ServerLinkViewModel(characterId, addr, display, state, onDecouple, onViewTrust));
        }
        return links;
    }

    /// <summary>The pinned TLS cert fingerprint for a server, shown in the trust dialog.</summary>
    public string? GetServerFingerprint(string serverAddress) =>
        _services?.GetRequiredService<IServerTrustStore>().GetFingerprint(serverAddress);

    /// <summary>Show the server info/trust dialog for a coupled-server link. Returns true if the
    /// user pressed Decouple inside it.</summary>
    public async Task<bool> ShowServerTrustAsync(ServerLinkViewModel link)
    {
        if (_dialogs is null) return false;
        var fingerprint = GetServerFingerprint(link.Address) ?? "";
        return await _dialogs.ShowServerTrustAsync(link.DisplayName, link.Address, fingerprint, link.StatusLabel);
    }

    /// <summary>
    /// Decouple a character from one server: revoke the server session (Session.Revoke, cuts the
    /// bus stream), drop the local session, then either detach the bus connection (no characters left on
    /// that server) or re-attach it with a remaining character's session.
    /// </summary>
    public async Task DecoupleAsync(ServerLinkViewModel link)
    {
        if (_dialogs is null || _coupling is null) return;
        if (!await _dialogs.ConfirmAsync(
                "Decouple",
                $"Decouple this character from {link.DisplayName}? The server session is revoked.",
                okText: "Decouple"))
            return;

        await _coupling.DecoupleCharacterAsync(link.Address, link.CharacterId);

        ActivityStatus = $"Decoupled from {link.DisplayName}.";
        await RefreshCharactersAsync();
        await RefreshFittingsTabsAsync();
    }

    // ── Fittings ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Import: pick which character, fetch its fits from ESI, then pick which fits to store.
    /// </summary>
    [RelayCommand]
    private async Task ImportFittings()
    {
        if (_services is null || _dialogs is null) return;

        // 1. Pick the character to import for (only chars with read_fittings + a local token are selectable).
        var charId = await _dialogs.PickCharacterAsync(
            "Import fits for which character?",
            BuildPickOptions(FittingsScopeCatalog.ReadFittings));
        if (charId is null) { FittingsStatus = "Import cancelled."; return; }

        var tokenStore = _services.GetRequiredService<IPerCharacterTokenStore>();
        var tokens = await tokenStore.LoadAsync(charId.Value);
        if (tokens is null) { FittingsStatus = "No token for that character — sign in first."; return; }

        // 2. Fetch from ESI.
        IReadOnlyList<EsiFitting> fits;
        FittingsStatus = "Fetching fits from ESI…";
        try
        {
            var esiClient = _services.GetRequiredService<IFittingEsiClient>();
            fits = await esiClient.GetFittingsAsync(charId.Value, tokens.AccessToken);
        }
        catch (Exception ex)
        {
            FittingsStatus = $"Fetch failed: {ex.Message}";
            return;
        }

        if (fits.Count == 0) { FittingsStatus = "No fits found on EVE."; return; }

        // 3. Show the import dialog (checkboxes). Null = cancelled.
        var selectedIds = await _dialogs.SelectFittingsAsync(fits);
        if (selectedIds is null) { FittingsStatus = "Import cancelled."; return; }
        if (selectedIds.Count == 0) { FittingsStatus = "Nothing selected."; return; }

        // 4. Store the selected fits.
        FittingsStatus = $"Importing {selectedIds.Count} fit(s)…";
        using var scope = _services.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
        var result = await dispatcher.Send(new ImportFittingsFromEsiCommand(charId.Value, fits, selectedIds));

        if (result.IsSuccess)
        {
            // Surface content-hash dedup skips (2026-06-04): each carries which existing fit it matched.
            var skipped = result.Messages.Where(m => m.Code == MessageCodes.Duplicate).ToList();
            FittingsStatus = skipped.Count == 0
                ? $"Imported {result.Value} fit(s)."
                : $"Imported {result.Value} fit(s); skipped {skipped.Count} duplicate(s): {string.Join(" ", skipped.Select(m => m.Text))}";
            await LoadFittingsAsync(); // global Local list
        }
        else
        {
            FittingsStatus = $"Import failed: {result.Messages.FirstOrDefault()?.Text}";
        }
    }

    /// <summary>Import a fit from pasted EFT/DNA text: parse + SDE-resolve + store in the Local library. Returns
    /// the imported (or matched-duplicate) fit name, or null on cancel/failure.</summary>
    private async Task<string?> ImportFitText()
    {
        if (_dialogs is null) return null;
        return await ImportFitFromTextAsync(await _dialogs.ImportFitTextAsync());
    }

    /// <summary>Import a fit from an eveship.fit (ESF) link: the link decodes through the same text
    /// importer (<see cref="ImportFitFromTextCommand"/> → EveshipFitCodec), so only the input window differs.</summary>
    private async Task<string?> ImportFitEsfLink()
    {
        if (_dialogs is null) return null;
        return await ImportFitFromTextAsync(await _dialogs.ImportFitEsfLinkAsync());
    }

    /// <summary>Shared parse + store + status for a pasted fit, whether it came from the EFT/DNA window or the ESF-link
    /// window. The importer auto-detects EFT, DNA and eveship.fit links. Returns the stored/matched fit name
    /// on success (so the caller can open its detail), null otherwise.</summary>
    private async Task<string?> ImportFitFromTextAsync(string? text)
    {
        if (_services is null) return null;
        if (string.IsNullOrWhiteSpace(text)) { FittingsStatus = "Import cancelled."; return null; }

        FittingsStatus = "Parsing fit…";
        using var scope = _services.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
        var result = await dispatcher.Send(new ImportFitFromTextCommand(text));

        if (!result.IsSuccess)
        {
            FittingsStatus = $"Import failed: {result.Messages.FirstOrDefault()?.Text}";
            return null;
        }

        // A duplicate isn't stored (handler dedups on content hash) → tell the user with a visible message and do not
        // open anything.
        if (result.Messages.Any(m => m.Code == MessageCodes.Duplicate))
        {
            FittingsStatus = $"Already in your library: '{result.Value}'.";
            if (_dialogs is not null)
                await _dialogs.ShowMessageAsync("Fit already imported",
                    $"'{result.Value}' is already in your Local library, so nothing was imported.");
            return null;
        }

        var skippedItems = result.Messages.Count(m => m.Severity == MessageSeverity.Warning);
        FittingsStatus = skippedItems == 0
            ? $"Imported '{result.Value}'."
            : $"Imported '{result.Value}' — {skippedItems} unknown item(s) skipped.";
        await LoadFittingsAsync();
        return result.Value;
    }

    /// <summary>Whether the fit detail should pop open right after a single-fit import.
    /// Default on; toggled in Settings.</summary>
    private async Task<bool> ShouldOpenDetailAfterImportAsync()
    {
        if (_services is null) return false;
        using var scope = _services.CreateScope();
        var settings = await scope.ServiceProvider.GetRequiredService<IDispatcher>().Query(new GetSettingsQuery());
        return settings.FirstOrDefault(s => s.Key == OpenDetailAfterImportSettingKey)?.Value != "false"; // default on
    }

    private async Task DownloadServerFit(SharedFitInfo sf)
    {
        if (_services is null) return;
        var repo = _services.GetRequiredService<IFittingRepository>();

        // Content-hash dedup (2026-06-04): if the same fit is already in the local library, don't download a duplicate
        // — tell the user which fit it matched instead.
        var contentHash = EveUtils.Shared.Modules.Fittings.FitContentHash.Compute(sf.RawJson);
        var duplicate = await repo.FindByContentHashAsync(contentHash);
        if (duplicate is not null)
        {
            FittingsStatus = $"Already have '{sf.Name}' locally as '{duplicate.Name}' — not downloaded again.";
            return;
        }

        await repo.UpsertAsync(new EveUtils.Shared.Modules.Fittings.Entities.LocalFitting
        {
            OwnerId = sf.SharedByCharacterName,   // display source
            EsiFittingId = sf.EsiFittingId,
            Name = sf.Name,
            ShipTypeId = sf.ShipTypeId,
            RawJson = sf.RawJson,
            ContentHash = contentHash,
            ImportedAt = DateTimeOffset.UtcNow
        });
        FittingsStatus = $"Downloaded '{sf.Name}' to local library.";
        await LoadFittingsAsync(); // reflect the download in the Local tab
    }

    /// <summary>Delete a fit from a server's shared library — confirmed first.</summary>
    private async Task DeleteServerFitFromTab(ServerFitRowViewModel row, FittingsTabViewModel tab)
    {
        if (_fitShare is null || _dialogs is null || tab.ServerAddress is null) return;
        if (!await _dialogs.ConfirmAsync("Delete from server",
                $"Delete '{row.Name}' from the server's shared library? This affects everyone."))
            return;

        var (accepted, message) = await _fitShare.DeleteSharedFitAsync(tab.ServerAddress, row.Fit.ServerId);
        if (accepted)
        {
            tab.ServerFits.Remove(row);
            tab.Status = $"Deleted '{row.Name}'.";
        }
        else
        {
            await _dialogs.ShowMessageAsync("Delete not allowed", message);
        }
    }

    /// <summary>Push: pick the target character, then push the fit to EVE for that character.</summary>
    [RelayCommand]
    private async Task PushFitting(FittingViewModel? fitting)
    {
        if (_fitExportActions is null || fitting is null) return;
        await _fitExportActions.PushToEveAsync(BuildExportRequest(fitting));
    }

    /// <summary>
    /// Share a fit to a server via the synchronous gRPC call so we get a real accept/deny result.
    /// Fits are local and shared across all characters, so the fit's source character does NOT
    /// influence the target — the choice is purely "which coupled server": exactly one coupled
    /// server → automatic, more than one → ask which. The server attributes the share to the identity
    /// coupled to that server (the session), not to the fit owner.
    /// </summary>
    [RelayCommand]
    private async Task ShareFitting(FittingViewModel? fitting)
    {
        if (_fitExportActions is null || fitting is null) return;
        await _fitExportActions.ShareToServerAsync(BuildExportRequest(fitting));
    }

    /// <summary>Loads the global Local fittings list (all characters; source char shown as a label).</summary>
    private async Task LoadFittingsAsync()
    {
        if (_services is null) return;
        using var scope = _services.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
        var list = await dispatcher.Query(new GetFittingsQuery());
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Fittings.Clear();
            foreach (var f in list)
            {
                var ownerName = Characters.FirstOrDefault(c => c.OwnerId == f.OwnerId)?.Name ?? f.OwnerId;
                Fittings.Add(new FittingViewModel(f, ownerName, PushFittingCb, ShareFittingCb, DeleteFittingCb, ExportFittingCb));
            }
        });
    }

    /// <summary>
    /// Rebuilds the fittings tabs: the Local tab stays first, then one tab per coupled server.
    /// Server tabs are created collapsed and load their fits lazily on first selection.
    /// </summary>
    private async Task RefreshFittingsTabsAsync()
    {
        if (_services is null) return;
        var sessionStore = _services.GetRequiredService<IClientSessionStore>();
        var servers = await sessionStore.ListServersAsync();

        var named = new List<(string Address, string Display)>();
        foreach (var addr in servers)
            named.Add((addr, _serverRegistry is null ? addr : await _serverRegistry.DisplayNameAsync(addr)));

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var previous = SelectedFittingsTab?.ServerAddress;
            FittingTabs.Clear();
            FittingTabs.Add(_localFitsTab);
            foreach (var (addr, display) in named)
                FittingTabs.Add(new FittingsTabViewModel(display, addr, LoadServerFitsTabAsync));
            SelectedFittingsTab = FittingTabs.FirstOrDefault(t => t.ServerAddress == previous) ?? _localFitsTab;
        });
    }

    /// <summary>Lazy loader for a server tab: fetches that server's shared fits.</summary>
    private async Task LoadServerFitsTabAsync(FittingsTabViewModel tab)
    {
        if (_fitShare is null || tab.ServerAddress is null) return;
        if (_busConnector?.StateFor(tab.ServerAddress) != ServerConnectionState.Connected)
        {
            tab.Status = "Not connected — couple a character to this server first.";
            return;
        }

        tab.Status = "Fetching server fits…";
        var (ok, message, serverFits) = await _fitShare.GetSharedFitsAsync(tab.ServerAddress);
        if (!ok) { tab.Status = $"Server fits unavailable: {message}"; return; }

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            tab.ServerFits.Clear();
            foreach (var sf in serverFits)
                tab.ServerFits.Add(new ServerFitRowViewModel(sf,
                    onDownload: row => DownloadServerFit(row.Fit),
                    onDelete:   row => DeleteServerFitFromTab(row, tab)));
            tab.Status = serverFits.Count == 0 ? "No fits shared on this server yet." : $"{serverFits.Count} shared fit(s).";
        });
    }

    private async Task PushFittingCb(FittingViewModel vm) => await PushFitting(vm);
    private async Task ShareFittingCb(FittingViewModel vm) => await ShareFitting(vm);
    private async Task DeleteFittingCb(FittingViewModel vm) => await DeleteLocalFitting(vm);
    private async Task ExportFittingCb(FittingViewModel vm) => await ExportFitting(vm);

    /// <summary>Export a stored fit to EFT + DNA text: open the export window via the shared seam.</summary>
    private async Task ExportFitting(FittingViewModel? fitting)
    {
        if (_fitExportActions is null || fitting is null) return;
        await _fitExportActions.OpenEftWindowAsync(BuildExportRequest(fitting));
    }

    /// <summary>
    /// Builds the per-call request for the shared fit export actions: the fit identity, the
    /// character-picker source (<see cref="BuildPickOptions"/>), the status sink, and the server-tab refresh
    /// the Local tab owns after a successful share.
    /// </summary>
    private FitExportRequest BuildExportRequest(FittingViewModel fitting) =>
        new(fitting.Id, fitting.Name,
            BuildPickOptions,
            status => FittingsStatus = status,
            OnSharedToServer: RefreshServerFitsTabAsync);

    private async Task RefreshServerFitsTabAsync(string targetAddress)
    {
        var tab = FittingTabs.FirstOrDefault(t => t.ServerAddress == targetAddress);
        if (tab is not null) { tab.IsLoaded = false; await tab.EnsureLoadedAsync(); }
    }

    /// <summary>Edit a local fit's metadata (fit-metadata) from the fit-browser: prompt with the current name/
    /// description/tags, persist the edit (modules + identity untouched), then refresh the Local tab.</summary>
    // Returns the edited draft (or null on cancel/missing) so an in-place caller — the fit-detail window — can refresh its
    // own header; the fit-browser row callers ignore the return and just rely on the reload.
    private async Task<FitMetadataDraft?> EditLocalFitMetadataAsync(int localFitId, Func<Task> reload)
    {
        if (_services is null || _dialogs is null) return null;
        var repo = _services.GetRequiredService<IFittingRepository>();
        var fit = await repo.FindByIdAsync(localFitId);
        if (fit is null) return null;

        var edited = await _dialogs.EditFitMetadataAsync(new FitMetadataDraft(fit.Name, fit.Description, fit.Tags));
        if (edited is null) return null;

        await repo.UpdateMetadataAsync(localFitId, edited.Name, edited.Description, edited.Tags);
        FittingsStatus = $"Updated '{edited.Name}'.";
        await reload();
        return edited;
    }

    /// <summary>Delete a local fit from the fit-browser by id — confirmed first — then refresh the Local tab.</summary>
    private async Task DeleteLocalFitByIdAsync(int localFitId, Func<Task> reload)
    {
        if (_services is null || _dialogs is null) return;
        var repo = _services.GetRequiredService<IFittingRepository>();
        var fit = await repo.FindByIdAsync(localFitId);
        if (fit is null) return;

        if (!await _dialogs.ConfirmAsync("Delete fitting",
                $"Remove '{fit.Name}' from your local library? This does not touch EVE or the server.", okText: "Delete"))
            return;

        await repo.RemoveByIdAsync(localFitId);
        FittingsStatus = $"Deleted '{fit.Name}' locally.";
        await reload();
    }

    /// <summary>Delete a fit from the local library — confirmed first.</summary>
    private async Task DeleteLocalFitting(FittingViewModel fitting)
    {
        if (_services is null || _dialogs is null) return;
        if (!await _dialogs.ConfirmAsync("Delete fitting",
                $"Remove '{fitting.Name}' from your local library? This does not touch EVE or the server."))
            return;

        var repo = _services.GetRequiredService<IFittingRepository>();
        await repo.RemoveByIdAsync(fitting.Id);
        FittingsStatus = $"Deleted '{fitting.Name}' locally.";
        await LoadFittingsAsync();
    }

    private void OnFitShared(FitSharedEvent evt) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            FittingsStatus = $"Fit shared: '{evt.Data.Name}' by {evt.Data.SharedByCharacterName}.");

    // ── Existing features (unchanged) ────────────────────────────────────────────────────────────

    private void OnCombat(CombatLoggedEvent integrationEvent) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() => RouteSample(integrationEvent.Data));

    private void RouteSample(DpsSampleDto sample)
    {
        var character = string.IsNullOrWhiteSpace(sample.CharacterName) ? "Unknown" : sample.CharacterName;
        var tracker = GetOrCreateTracker(character);

        // Local characters are scrolled smoothly by the ~30fps render timer; only relay remote-member
        // samples here, which have no local tracker to sample from.
        if (_gamelog is null || !_gamelog.HasLocalTracker(character))
            tracker.Apply(sample);
    }

    /// <summary>The live DPS tracker for a character, created (and added to the list) on first use so an overlay
    /// popped before any sample arrives shares the same instance and fills in live.</summary>
    private DpsViewModel GetOrCreateTracker(string character)
    {
        if (!_trackersByCharacter.TryGetValue(character, out var tracker))
        {
            var isSelf = string.Equals(character, _localCharacter, StringComparison.OrdinalIgnoreCase);
            tracker = new DpsViewModel(character, isSelf);
            _trackersByCharacter[character] = tracker;
            if (isSelf) DpsTrackers.Insert(0, tracker);
            else DpsTrackers.Add(tracker);

            // Drive the meter through the shared 30fps driver. A LOCAL character (its gamelog is tailed here — i.e. it
            // has been observed locally, even before it has fought) samples its live decaying combat rates from the
            // gamelog each frame, so its graph scrolls and comes alive on the first hit just like a fleet meter, and
            // never waits for a server round-trip. Only a purely remote member (seen via a relayed sample, never
            // observed locally) returns null so its event-driven Apply path owns the series.
            tracker.UseSampler(() => _gamelog is { } gamelog && (_observedCharacters.Contains(character) || gamelog.HasLocalTracker(character))
                ? gamelog.SampleCombat(character)
                : (EveUtils.Shared.Modules.Gamelog.Aggregation.CombatRates?)null);
            _renderDriver?.Register(tracker);
            ApplyGamelogMetrics(tracker); // seed bounty/location so a freshly popped overlay isn't blank until the next change
        }
        return tracker;
    }

    // Pull bounty + last-known system from the gamelog onto every locally-tracked character, so the shared
    // DpsViewModel a pop-out overlay binds to carries them — for ALL my multiboxed characters, not just the one
    // that happens to be signed in (IsSelf). Driven by the gamelog's MetricsChanged (a bounty payout / jump).
    private void RefreshSelfTrackerMetrics()
    {
        foreach (var tracker in _trackersByCharacter.Values)
            ApplyGamelogMetrics(tracker);
    }

    // Bounty + location belong to MY own (locally tailed) characters; a purely remote fleet member has neither here.
    private void ApplyGamelogMetrics(DpsViewModel tracker)
    {
        if (_gamelog is null ||
            (!_observedCharacters.Contains(tracker.Character) && !_gamelog.HasLocalTracker(tracker.Character)))
            return;
        var snapshot = _gamelog.Snapshot(tracker.Character);
        tracker.Bounty = snapshot.BountyTotal;
        if (!string.IsNullOrWhiteSpace(snapshot.Location))
            tracker.Location = snapshot.Location;
    }

    private void OnShipAdded(ShipAddedEvent integrationEvent)
    {
        var dto = integrationEvent.Data;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (!Ships.Any(s => s.Id == dto.Id && s.Name == dto.Name))
                Ships.Add(dto);
        });
    }

    [RelayCommand]
    private void ToggleFeed()
    {
        if (_feeder is null) return;
        if (_feedCts is not null)
        {
            _feedCts.Cancel();
            _feedCts.Dispose();
            _feedCts = null;
            FeedButtonText = "START FEED";
            return;
        }
        _feedCts = new CancellationTokenSource();
        FeedButtonText = "STOP FEED";
        _ = _feeder.RunAsync(_feedCts.Token);
    }

    /// <summary>
    /// Couple a character to a server: ask for the address + optional label, query the server's
    /// optional scopes, run the SSO pairing, then attach the bus and refresh the per-character links/tabs.
    /// </summary>
    /// <summary>
    /// Couple a character to a server: ask for the address + optional label, query the server's
    /// optional scopes, run the SSO pairing, then attach the bus and refresh links/tabs. Returns true if a
    /// server was coupled, false if the user cancelled or it failed. Invoked from a character's settings dialog;
    /// the server decides which character from EVE's signed token.
    /// </summary>
    /// <summary>Unauthenticated probe for the couple dialog: returns the server's own name, or null
    /// if unreachable. Reuses the accept-any-cert scopes probe; display-only (real trust = TOFU at pairing).</summary>
    private async Task<string?> ProbeServerNameAsync(string address, CancellationToken cancellationToken)
    {
        if (_pairing is null) return null;
        var scopes = await _pairing.GetServerScopesAsync(address, cancellationToken);
        return scopes?.ServerName;
    }

    public async Task<bool> RunCoupleAsync()
    {
        if (_pairing is null || _dialogs is null) return false;

        var couple = await _dialogs.CoupleServerAsync(ProbeServerNameAsync);
        if (couple is null) { ActivityStatus = "Coupling cancelled."; return false; }
        var address = couple.Address;

        try
        {
            // Record the user label now so the UI can show it even before pairing fills in the server name.
            if (_serverRegistry is not null)
                await _serverRegistry.SetAsync(address, couple.Label, serverName: null);

            // ask the server which optional scopes it wants, let the user opt in before pairing.
            var serverScopes = await _pairing.GetServerScopesAsync(address);
            var scopes = new List<string>(serverScopes?.RequiredScopes ?? ["publicData"]);

            if (serverScopes is { OptionalScopes.Count: > 0 })
            {
                var optional = serverScopes.OptionalScopes
                    .Select(o => new EsiScopeRequirement(o.Scope, EsiScopeTarget.Server, o.Feature, o.Reason))
                    .ToList();
                var chosen = await _dialogs.SelectScopesAsync(optional);
                if (chosen is null) { ActivityStatus = "Pairing cancelled."; return false; }
                scopes.AddRange(chosen);
            }

            var result = await _pairing.PairAsync(address, scopes, status => ActivityStatus = status);
            // Remember the server's own name so the UI can show it (or the label) instead of the URL.
            if (_serverRegistry is not null)
                await _serverRegistry.SetAsync(address, label: null, serverName: result.ServerName);
            if (_busConnector is not null)
                await _busConnector.AttachAsync(address, result.CharacterId); // attach with the just-paired char's session

            var affiliation = string.IsNullOrEmpty(result.AllianceName)
                ? result.CorporationName
                : $"{result.CorporationName} · {result.AllianceName}";
            var suffix = string.IsNullOrWhiteSpace(affiliation) ? "" : $" ({affiliation})"; // no empty "()"
            ActivityStatus = $"Connected to {result.ServerName} as {result.CharacterName}{suffix}";
            await RefreshCharactersAsync(); // reflect the cloud-synced state on the paired character(s)
            await RefreshFittingsTabsAsync(); // add the new server's fits tab
            return true;
        }
        catch (Exception ex)
        {
            ActivityStatus = $"Pairing failed: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Sign in (= add or update a character): show the scope-selection dialog, then run the
    /// EVE SSO with exactly the chosen scopes. Every sign-in adds/updates a character in the registry,
    /// so there is just one action — no separate "add character".
    /// </summary>
    private async Task SignInWithScopeDialogAsync(bool isNew)
    {
        if (_login is null || _dialogs is null || _scopeRegistry is null) return;

        // 1. Let the user pick which scopes to request (defaults to all, from the registry).
        var available = _scopeRegistry.GetRequirements(EsiScopeTarget.Client);
        var selected = await _dialogs.SelectScopesAsync(available);
        if (selected is null)
        {
            ActivityStatus = "Sign-in cancelled.";
            return; // user closed the dialog
        }

        // 2. Run the SSO with the chosen scopes.
        _signInCts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        IsSigningIn = true;
        try
        {
            ActivityStatus = "Signing in… (cancel to abort)";
            var identity = await _login.SignInAsync(selected, _signInCts.Token);
            ActivityStatus =$"Signed in: {identity.CharacterName} ({identity.CharacterId})";
            _localCharacter = identity.CharacterName;
            if (_gamelog is not null)
            {
                _gamelog.SetCharacter(identity.CharacterName);
                _gamelog.MapCharacter(identity.CharacterId, identity.CharacterName); // couple id↔name for fleet DPS
            }
            await RefreshCharactersAsync();
        }
        catch (OperationCanceledException)
        {
            ActivityStatus = "Sign-in cancelled.";
        }
        catch (Exception ex)
        {
            ActivityStatus =$"Sign-in failed: {ex.Message}";
        }
        finally
        {
            IsSigningIn = false;
            _signInCts?.Dispose();
            _signInCts = null;
        }
    }

    [RelayCommand]
    private void CancelSignIn()
    {
        _signInCts?.Cancel();
    }

    [RelayCommand]
    private async Task SimulateHit()
    {
        if (_gamelog is null) return;
        var direction = _outgoing ? DamageDirection.Outgoing : DamageDirection.Incoming;
        _outgoing = !_outgoing;
        await _gamelog.AddHitAsync(direction, _random.Next(80, 520), "Guristas Scout");
    }

    [RelayCommand]
    private async Task Add()
    {
        if (_services is null || string.IsNullOrWhiteSpace(NewShipName)) return;
        using var scope = _services.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
        await dispatcher.Send(new AddShipCommand(NewShipName, "Frigate", 1_000_000m));
    }

    private async Task LoadAsync()
    {
        if (_services is null) return;
        using var scope = _services.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        Ships.Clear();
        foreach (var ship in await dispatcher.Query(new GetShipsQuery()))
            Ships.Add(ship);

        Settings.Clear();
        foreach (var setting in await dispatcher.Query(new GetSettingsQuery()))
            Settings.Add(setting);

        // Restore the persisted shell prefs: dock mode (default Docked) + collapsed character column.
        if (Settings.FirstOrDefault(s => s.Key == DockModeSettingKey)?.Value == "floating")
            IsFloating = true;
        if (Settings.FirstOrDefault(s => s.Key == CharsCollapsedSettingKey)?.Value == "true")
            IsCharsCollapsed = true;

        // Apply the persisted toast position (default TopRight) so notifications appear where the user configured them.
        if (_services.GetService<Notifications.ToastService>() is { } toastService)
            toastService.ApplyPositionSetting(Settings.FirstOrDefault(s => s.Key == Notifications.ToastService.PositionSettingKey)?.Value);

        // Re-attach to paired servers first: this only does a quick session lookup and hands the actual
        // connect off to a background loop (0 s initial backoff), so the connection starts immediately instead
        // of waiting behind the per-character ESI token refresh below. The connection uses the stored pairing
        // session, not the ESI tokens, so there is no ordering dependency on the steps that follow.
        await RestoreServerConnectionsAsync(); // re-attach the event bus to paired servers on startup

        await RefreshCharactersAsync();
        await CheckTokensOnStartupAsync();     // refresh/flag ESI tokens at startup
        await LoadFittingsAsync();             // global Local fittings list (all characters)
        await RefreshFittingsTabsAsync();      // server tabs for the restored connections
        await Home.RefreshAsync();             // home dashboard: your fleets, latest shared fits, character stats
    }

    /// <summary>
    /// Kicks off the SDE update check independently of <see cref="LoadAsync"/> (the window's Opened event drives
    /// it, so the modal has a shown owner). Decoupled on purpose: a slow/failing startup step (e.g. an unreachable
    /// paired server) must never swallow the check, and its own failure is surfaced instead of silently lost.
    /// </summary>
    public void StartSdeUpdateCheck() => _ = RunSdeUpdateCheckResilientAsync();

    private async Task RunSdeUpdateCheckResilientAsync()
    {
        try
        {
            await CheckSdeUpdateAsync();
        }
        catch (Exception ex)
        {
            ActivityStatus = $"SDE update check failed: {ex.Message}";
        }
    }

    /// <summary>
    /// On startup, if a newer (or missing) SDE build is available, ask the user once and — on accept — run the
    /// import behind a progress popup that closes itself when done. Offline/unreachable CCP is non-fatal: skip
    /// silently and keep using whatever store exists. The server does this autonomously with no UI.
    /// </summary>
    private async Task CheckSdeUpdateAsync()
    {
        if (_services is null || _dialogs is null) return;

        var importer = _services.GetRequiredService<ISdeImporter>();
        SdeUpdateCheck check;
        try
        {
            check = await importer.CheckForUpdateAsync();
        }
        catch
        {
            return; // CCP unreachable / offline — the existing store (if any) keeps working.
        }

        if (!check.UpdateAvailable) return;

        var message = check.Local is null
            ? $"EVE static data (build {check.Remote.BuildNumber}) is needed for item names and fittings. " +
              "Download it now? (~80 MB)"
            : $"A newer EVE static data build ({check.Remote.BuildNumber}) is available. Update now? (~80 MB)";
        if (!await _dialogs.ConfirmAsync("EVE static data", message, okText: "Update"))
            return;

        await RunSdeImportPopupAsync();
    }

    /// <summary>
    /// Runs a forced SDE (re)import behind the progress popup. Shared by the startup prompt and the Settings
    /// "Re-download &amp; re-import" button (fallback/debug). The popup closes itself when done.
    /// </summary>
    private async Task RunSdeImportPopupAsync()
    {
        if (_services is null || _dialogs is null) return;
        var importer = _services.GetRequiredService<ISdeImporter>();
        var progress = new SdeProgressViewModel();
        var importTask = importer.ImportAsync(progress); // reports into the popup; runs the build off-thread
        await _dialogs.ShowSdeUpdateAsync(progress);     // modal; closes itself when the VM signals done
        await importTask;                                // observe the outcome (errors already surfaced in the popup)
    }

    /// <summary>A human label for the currently loaded SDE build, shown in Settings ("Not downloaded yet" if none).</summary>
    private string SdeVersionLabel()
    {
        var version = _services?.GetService<ISdeAccessor>()?.Version;
        return version is null
            ? "Not downloaded yet"
            : $"build {version.BuildNumber} (released {version.ReleaseDate:yyyy-MM-dd})";
    }

    /// <summary>
    /// On startup, re-attach the remote event bus to a previously paired server so the connection is
    /// restored without re-pairing. The session token (~1h) from the last pairing is reused; if it has
    /// expired or the server is down, this fails gracefully (status message, no crash).
    /// </summary>
    private async Task RestoreServerConnectionsAsync()
    {
        if (_services is null || _busConnector is null) return;

        var sessionStore = _services.GetRequiredService<IClientSessionStore>();
        // Reconnect every server we have a session for, not just one. Each gets its own managed
        // connect-loop; the StateChanged handler drives the per-server indicators (connecting → connected,
        // or session-expired → re-pair).
        foreach (var server in await sessionStore.ListServersAsync())
            await _busConnector.AttachAsync(server);
    }
}
