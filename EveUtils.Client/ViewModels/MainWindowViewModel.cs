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
using EveUtils.Client.Clipboard;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Fittings;
using EveUtils.Client.Notifications;
using EveUtils.Client.ViewModels.FitBrowser;
using EveUtils.Client.Esi;
using EveUtils.Client.EveSettings;
using EveUtils.Client.Platform;
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
using EveUtils.Shared.Modules.Market.Services;
using EveUtils.Client.Pairing;
using EveUtils.Client.Theming;
using EveUtils.Client.Transport;
using EveUtils.Client.Updates;
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
    private readonly ClipboardWatchService? _clipboardWatch;
    private readonly EsiTokenStatusTracker? _tokenStatus;
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

    // Same banner slot, for a server pairing that has lapsed (ET-77). Persistent on purpose: a list read against a
    // server that no longer accepts the session comes back EMPTY rather than failing, so without this the app would
    // go on quietly showing "there is nothing here" for as long as the pairing stays broken.
    [ObservableProperty] private bool _isServerPairingAlert;
    [ObservableProperty] private string _serverPairingAlertMessage = "";

    // And again for a server whose TLS certificate no longer matches its pin (ET-95). Its own slot rather than a
    // second reason for the one above: this one carries the fingerprints the user has to compare, and the two can
    // stand at once (one server refused, another lapsed) without either message swallowing the other.
    [ObservableProperty] private bool _isServerCertificateAlert;
    [ObservableProperty] private string _serverCertificateAlertMessage = "";

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

    /// <summary>Clipboard-watch state on the bottom bar. It is in the always-visible strip on purpose: a feature
    /// that can see everything you copy has to say at a glance that it is off, without opening settings first.</summary>
    [ObservableProperty] private string _clipboardStatus = "CLIPBOARD OFF";

    /// <summary>True while the clipboard is actually being watched — the bar highlights that state.</summary>
    [ObservableProperty] private bool _isClipboardWatching;

    public string ClipboardStatusTooltip => IsClipboardWatching
        ? "EVE Together is watching the clipboard for fits and inventory listings. Settings → Privacy & Sharing explains what it reads and what it drops."
        : "EVE Together is not reading your clipboard. Turn it on in Settings → Privacy & Sharing.";

    partial void OnIsClipboardWatchingChanged(bool value) => OnPropertyChanged(nameof(ClipboardStatusTooltip));

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
    public bool IsToolsActive => ActiveModule == "tools";

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
        OnPropertyChanged(nameof(IsToolsActive));
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
            case "settings-sync": OpenSettingsSync(); break;
            case "appraisal": OpenAppraisal(); break;
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

        // Live bus state → the server link indicators. Per CHARACTER, not per server: the roll-up answers "is this
        // server usable at all", and a character whose session the server dropped is invisible in it as soon as one
        // other character on the same server is healthy (ET-123).
        if (_busConnector is not null)
            _busConnector.CharacterStateChanged += (address, characterId, state) =>
                Avalonia.Threading.Dispatcher.UIThread.Post(() => ApplyServerConnectionState(address, characterId, state));

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

        // Live ESI session state per character → that row's ESI chip. Every token check (the 60 s loop, every
        // ESI call, a sign-in) lands here, so the chip follows the real token instead of waiting for a restart.
        // Rebuilt rows re-seed from the same tracker in RefreshCharactersAsync — see ET-24.
        _tokenStatus = services.GetRequiredService<EsiTokenStatusTracker>();
        _tokenStatus.Changed += (characterId, status) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => _ApplyTokenStatus(characterId, status));

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

        // Clipboard watch state → the bottom bar. Seeded here and followed live, so flipping the setting updates
        // the strip without a restart (ET-57).
        _clipboardWatch = services.GetRequiredService<ClipboardWatchService>();
        _ApplyClipboardState();
        _clipboardWatch.StateChanged += () =>
            Avalonia.Threading.Dispatcher.UIThread.Post(_ApplyClipboardState);

        // Smooth, demo-parity DPS graphs: every tracker (own + fleet) renders through the one shared
        // ~30fps DpsRenderDriver, so the curve scrolls + decays continuously and all graphs share one render path.
        _renderDriver = services.GetRequiredService<DpsRenderDriver>();

        _ = RunStartupResilientAsync();
    }

    // Marks every character row whose EVE client is currently running on this machine (matched on the window-title
    // name OR the launcher's character id — see EveClientEvidence for why both exist).
    private void _ApplyClientPresence(EveUtils.Client.Platform.EveClientEvidence evidence)
    {
        foreach (var vm in Characters)
            vm.HasActiveClient = evidence.Matches(vm.Name, vm.CharacterId);
    }

    /// <summary>The ESI scopes this build declares, so a view can name a granted one instead of showing its id.</summary>
    internal EveUtils.Shared.Modules.Esi.IEsiScopeRegistry? ScopeRegistry =>
        _services?.GetService<EveUtils.Shared.Modules.Esi.IEsiScopeRegistry>();

    // All three states lead to the same place: Privacy & Sharing holds the switch, the disclosure of what is read,
    // and — when the desktop cannot report a change — the explanation of why it is off. So every state has something
    // there to act on or to read, and picking a different destination per state would only change the scroll offset.
    [RelayCommand]
    private Task OpenClipboardSettings() => OpenSettings(Views.SettingsWindow.PrivacyCategory);

    // "Unsupported" is a state of its own rather than a second flavour of off: on a platform that cannot report a
    // clipboard change, showing OFF would suggest the switch does something.
    private void _ApplyClipboardState()
    {
        if (_clipboardWatch is null)
            return;

        IsClipboardWatching = _clipboardWatch.IsWatching;
        ClipboardStatus = !_clipboardWatch.IsSupported ? "CLIPBOARD UNSUPPORTED"
            : _clipboardWatch.IsWatching ? "CLIPBOARD WATCHING"
            : "CLIPBOARD OFF";
    }

    // Writes a measured token status onto the matching character row. The row is only a view of it — the value
    // itself lives in EsiTokenStatusTracker, keyed by character id, so a rebuild between two checks cannot lose it.
    private void _ApplyTokenStatus(int characterId, TokenStatus status)
    {
        foreach (var vm in Characters)
            if (vm.CharacterId == characterId)
                vm.EsiTokenStatus = status;
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

    /// <summary>Opens the EVE Settings Sync tool (the first entry under Tools) — non-modal, like the other
    /// modules. A fresh view-model per open so it reads the settings folder as it is right now.</summary>
    [RelayCommand]
    private void OpenSettingsSync()
    {
        if (_dialogs is null || _services is null)
            return;
        _dialogs.ShowSettingsSync(new SettingsSyncViewModel(
            _services.GetRequiredService<SettingsSyncService>(),
            _services.GetRequiredService<SettingsBackupService>(),
            _services.GetRequiredService<SettingsPresetService>(),
            _services.GetRequiredService<EveSettingsNameResolver>(),
            _services.GetRequiredService<EveSettingsPreferences>(),
            _services.GetRequiredService<EveClientPresenceService>(),
            _services.GetRequiredService<IEveSettingsWatch>(),
            _dialogs));
    }

    /// <summary>Opens the Appraisal tool (ET-83) — non-modal, like the other modules. A fresh view-model per open so
    /// it reads the price cache as it stands now rather than as it stood when the window was first built.</summary>
    [RelayCommand]
    private void OpenAppraisal()
    {
        if (_dialogs is null || _services is null)
            return;
        _dialogs.ShowAppraisal(new AppraisalViewModel(
            _services.GetRequiredService<IEnumerable<IAppraisalProvider>>(),
            _services.GetRequiredService<ISdeAccessor>(),
            _dialogs));
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
        FitBrowserViewModel viewModel = null!;
        // After a successful share, refresh the matching server tab so the shared fit shows up — one fit-browser
        // wide, so both the per-row share and the fit-detail window's share reach the same tab regardless of which
        // one triggered it (OnSharedToServer, FitExportActions.ShareToServerAsync).
        async Task RefreshServerTabAsync(string targetAddress)
        {
            var tab = viewModel.Tabs.FirstOrDefault(t => t.ServerAddress == targetAddress);
            if (tab is not null) await tab.ReloadAsync();
        }
        _refreshServerFitBrowserTab = RefreshServerTabAsync;

        async Task ReloadLocalAsync() => localTab.SetRows(await BuildLocalFitRowsAsync(names, EditFitMetadataAsync, DeleteFitAsync, RefreshServerTabAsync));
        async Task EditFitMetadataAsync(int localFitId) => await EditLocalFitMetadataAsync(localFitId, ReloadLocalAsync);
        async Task DeleteFitAsync(int localFitId) => await DeleteLocalFitByIdAsync(localFitId, ReloadLocalAsync);
        // Hand the Local-tab refresh to the detail window's in-place metadata edit (OpenFitDetailAsync, opened from here).
        _reloadLocalFits = ReloadLocalAsync;

        localTab = new FitBrowserTabViewModel(
            "Local library", await BuildLocalFitRowsAsync(names, EditFitMetadataAsync, DeleteFitAsync, RefreshServerTabAsync), names);
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

        // The browser is one module for the whole app (ET-48): re-opening FITS re-selects this standing instance and
        // calls this instead of building a fresh one, so a fit imported elsewhere or a server coupled meanwhile
        // still shows up — additive on the server tabs, same as ET-46's RefreshModule.
        async Task RefreshAsync()
        {
            await ReloadLocalAsync();
            var known = viewModel.Tabs.Where(t => !t.IsLocal).Select(t => t.ServerAddress).ToHashSet();
            foreach (var addr in await sessionStore.ListServersAsync())
            {
                if (known.Contains(addr)) continue;
                var display = _serverRegistry is null ? addr : await _serverRegistry.DisplayNameAsync(addr);
                viewModel.Tabs.Add(new FitBrowserTabViewModel(display, addr, LoadServerFitBrowserTabAsync, names));
            }
        }

        viewModel = new FitBrowserViewModel(
            tabs, OpenFitDetailAsync,
            importEsi: async () => { await ImportFittings(); await ReloadLocalAsync(); },
            importText: () => ImportThenMaybeOpenAsync(ImportFitText),
            importEsfLink: () => ImportThenMaybeOpenAsync(ImportFitEsfLink),
            refresh: RefreshAsync,
            loadSort: LoadFitBrowserSortAsync,
            saveSort: SaveFitBrowserSortAsync);
        _dialogs.ShowFitBrowser(viewModel);
    }

    /// <summary>The browser's remembered order, or null when it was never chosen (or was written by a newer
    /// client) — the Fit name default then stands.</summary>
    private async Task<FitSortChoice?> LoadFitBrowserSortAsync()
    {
        if (_services is null) return null;
        using var scope = _services.CreateScope();
        var settings = await scope.ServiceProvider.GetRequiredService<IDispatcher>().Query(new GetSettingsQuery());
        return FitSortChoice.Parse(settings.FirstOrDefault(s => s.Key == FitBrowserViewModel.SortSettingKey)?.Value);
    }

    private async Task SaveFitBrowserSortAsync(FitSortChoice choice)
    {
        if (_services is null) return;
        using var scope = _services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IDispatcher>()
            .Send(new SetSettingCommand(FitBrowserViewModel.SortSettingKey, choice.ToSetting()));
    }

    // Set when the fit-browser builds its Local tab; lets the detail window's in-place metadata edit refresh that tab.
    private Func<Task>? _reloadLocalFits;

    // Set when the fit-browser opens; lets the detail window's own "Share to server…" refresh the matching server
    // tab too, the same way a per-row share in the browser grid does.
    private Func<string, Task>? _refreshServerFitBrowserTab;

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
            onEditMetadata,                                           // in-place edit of the fit's name/notes/tags (local fits)
            _refreshServerFitBrowserTab);                             // refresh the browser's server tab after a share (null if the browser was never opened this session)
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
        ISdeNameResolver names, Func<int, Task>? onEditMetadata = null, Func<int, Task>? onDelete = null,
        Func<string, Task>? onSharedToServer = null)
    {
        using var scope = _services!.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
        var list = await dispatcher.Query(new GetFittingsQuery());

        var rows = new List<FitRowViewModel>();
        foreach (var f in list)
        {
            var fit = TryParseFit(f.RawJson);
            if (fit is null) continue;
            // The owning character, when there is one: it carries both the name shown as the uploader and the ESI id
            // the card's avatar needs. CharacterId is 0 for a gamelog-only pilot, and an imported fit may match no
            // character at all — the card then falls back to the uploader's initial.
            var owner = Characters.FirstOrDefault(c => c.OwnerId == f.OwnerId);
            // local DB id drives export; hull/module icons via the type-image provider; the per-row
            // export dropdown reaches the shared seam with the picker + status sink; edit/delete metadata
            // (fit-metadata) reach back to the browser composition through the callbacks
            var row = new FitRowViewModel(fit, owner?.Name ?? f.OwnerId, names, f.Id, _services!.GetService<ITypeImageProvider>(),
                _fitExportActions, BuildPickOptions, status => FittingsStatus = status,
                _services!.GetService<IMarketPriceRepository>(), onEditMetadata, onDelete, tags: f.Tags,
                portraits: _services!.GetService<ICharacterPortraitProvider>(),
                uploaderCharacterId: owner?.CharacterId ?? 0,
                onSharedToServer: onSharedToServer);
            // The images are the page's business, not the library's: the browser pulls them in for the fits it is
            // actually showing (FitBrowserTabViewModel.FillPage). A library of 148 fits used to fetch 148 renders
            // here, of which one page-worth was ever looked at.
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
                prices: _services!.GetService<IMarketPriceRepository>(),
                portraits: _services!.GetService<ICharacterPortraitProvider>(),
                uploaderCharacterId: sf.SharedByCharacterId);   // a shared fit always names a real character
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

    /// <summary>
    /// Updates the one link this state belongs to: this character's coupling to this server. It used to paint every
    /// link pointing at the address, which made a per-character state impossible to show in either direction — one
    /// character's trouble was either hidden by its neighbours or smeared over all of them.
    /// </summary>
    private void ApplyServerConnectionState(string address, int characterId, ServerConnectionState state)
    {
        foreach (var c in Characters)
            foreach (var link in c.ServerLinks)
                if (link.CharacterId == characterId && string.Equals(link.Address, address, StringComparison.OrdinalIgnoreCase))
                    link.State = state;

        RefreshServerPairingAlert();
    }

    /// <summary>Re-derives both server banners — a lapsed pairing and a refused certificate — from the links as they
    /// now stand. Called on every state change and after the character list is rebuilt, so a banner appears without
    /// waiting for a state change to happen to fire, and clears itself the moment the link is good again.</summary>
    public void RefreshServerPairingAlert()
    {
        var links = Characters.SelectMany(c => c.ServerLinks).ToList();

        // Carries the character's own name, because with six characters on one server naming only the server leaves
        // the reader to guess which of their pilots has to be dealt with (ET-123).
        var named = Characters
            .SelectMany(c => c.ServerLinks.Select(l => new ServerPairingAlert.Link(l.DisplayName, c.Name, l.State)))
            .ToList();

        (IsServerPairingAlert, ServerPairingAlertMessage) = ServerPairingAlert.For(named);

        (IsServerCertificateAlert, ServerCertificateAlertMessage) = ServerCertificateAlert.For(
            links.Where(l => l.State is ServerConnectionState.CertificateRejected)
                 .Select(l => new ServerCertificateAlert.RejectedCertificate(
                     l.DisplayName, GetServerFingerprint(l.Address), GetPresentedServerFingerprint(l.Address))));

        AnnounceRefusedServers(named);
    }

    // Servers already announced by a toast this run, so the slow retry behind a refused session cannot re-announce
    // itself every few minutes. A server that recovers is dropped from the set, so a second spell is told again.
    private readonly HashSet<string> _refusalAnnounced = new(StringComparer.OrdinalIgnoreCase);

    // The same, for sessions the server no longer has. Its own set: a link can pass from refused to gone, and one
    // shared set would swallow the second announcement — which is the one that asks the user to do something.
    private readonly HashSet<string> _sessionGoneAnnounced = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Raises the transition toast for a server that has just started refusing its stored session. The banner beside
    /// it carries the ongoing state; this is only the moment, and only on the window in front of the user — the
    /// banner lives on the main window, which is not necessarily the one they are looking at.
    /// </summary>
    private void AnnounceRefusedServers(IReadOnlyList<ServerPairingAlert.Link> links)
    {
        var gone = InState(links, ServerConnectionState.SessionGone);
        if (IsNewlyAnnounced(_sessionGoneAnnounced, Keys(gone)))
            Show(ServerLinkRefusalToast.ForSessionGone(gone), ServerLinkRefusalToast.SessionGoneReplacementKey);

        var goneKeys = Keys(gone).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var refused = InState(links, ServerConnectionState.SessionExpired)
            // A coupling whose session is gone is announced as gone and nothing else — the softer card would sit on
            // top of the one that actually asks for something.
            .Where(a => !goneKeys.Contains($"{a.Server} {a.Character}"))
            .ToList();
        if (IsNewlyAnnounced(_refusalAnnounced, Keys(refused)))
            Show(ServerLinkRefusalToast.For(refused), ServerLinkRefusalToast.ReplacementKey);

        void Show((string Title, string Message) card, string replacementKey) =>
            _services?.GetService<IToastService>()?.Show(
                card.Title, card.Message, ToastKind.Warning, [], onClosed: null, replacementKey: replacementKey);
    }

    private static List<(string Server, string Character)> InState(
        IReadOnlyList<ServerPairingAlert.Link> links, ServerConnectionState state) =>
        links
            .Where(l => l.State == state)
            .Select(l => (Server: l.ServerName, Character: l.CharacterName))
            .Distinct()
            .ToList();

    // Announced per (server, character) rather than per server: one character going quiet on a server another five
    // are happily using is its own event, and keying on the server alone would swallow it (ET-123).
    private static List<string> Keys(IEnumerable<(string Server, string Character)> affected) =>
        affected.Select(a => $"{a.Server} {a.Character}").ToList();

    /// <summary>Records the current names against what has already been announced and says whether any of them is
    /// new. Names that recovered drop out, so a second spell is told again. Every name is added rather than stopping
    /// at the first new one — short-circuiting would leave the rest unannounced-but-unrecorded, and they would toast
    /// on the very next state change.</summary>
    private static bool IsNewlyAnnounced(HashSet<string> announced, IReadOnlyList<string> current)
    {
        announced.IntersectWith(current);
        var isNew = false;
        foreach (var name in current)
            isNew |= announced.Add(name);
        return isNew;
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
    /// The per-row chips are not set here — <c>EnsureValidAsync</c> records every outcome on
    /// <c>EsiTokenStatusTracker</c>, which is what the rows read from, whether they survive this loop or not.
    /// </summary>
    private async Task CheckTokensOnStartupAsync()
    {
        if (_services is null) return;
        var refresher = _services.GetRequiredService<ClientTokenRefreshService>();

        var needReauth = new List<string>();
        // Iterate a copy: each check awaits, and a check that refreshes a token can write to the registry, which
        // rebuilds Characters (Clear + Add) mid-loop — enumerating the live collection would throw right here and
        // leave the remaining characters unchecked (the same reason LoadCharacterPortraitsAsync takes a copy).
        foreach (var c in Characters.ToList())
        {
            if (c.CharacterId <= 0) continue; // local-only gamelog row: no ESI identity, nothing to check

            var status = await refresher.EnsureValidAsync(c.CharacterId);
            if (status is TokenStatus.NeedsReauth)
                needReauth.Add(c.Name);
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
    /// <summary>
    /// Opens the settings window, optionally straight at one of its categories rather than at General.
    /// </summary>
    private async Task OpenSettings(int initialCategory = 0)
    {
        if (_dialogs is null || _services is null) return;

        string current;
        MetricShareSnapshot shares;
        bool loadImages;
        bool openDetailAfterImport;
        Notifications.ToastPosition toastPosition;
        bool localApiEnabled;
        int localApiPort;
        bool checkUpdatesOnStartup;
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
            checkUpdatesOnStartup = settings.FirstOrDefault(s => s.Key == CheckUpdatesOnStartupSettingKey)?.Value != "false"; // default on
        }

        var localApi = _services.GetService<LocalApi.ILocalApiServer>();
        var localApiStatusLabel = localApi is not null ? _LocalApiStatusLabel(localApi.Status) : "";
        _dialogs.ShowSettings(
            current, GameLogLocations.Default(),
            shares.IsShared(MetricKind.Location), shares.IsShared(MetricKind.Bounty), shares.IsShared(MetricKind.Dps),
            loadImages, _theme?.Current ?? FactionTheme.Gallente, SdeVersionLabel(), ApplySettingsAsync, openDetailAfterImport, toastPosition,
            localApiEnabled, localApiPort, localApiStatusLabel, localApi, checkUpdatesOnStartup, _clipboardWatch, initialCategory);
    }

    /// <summary>Opens the About dialog: app identity + version, creator credits with portraits,
    /// inspiration links, the AGPLv3 license and the mandatory CCP attribution.</summary>
    [RelayCommand]
    private async Task OpenAbout()
    {
        if (_dialogs is null) return;
        var characterInfo = _services?.GetService<ICharacterInfoService>();

        // About checks and reports; the offer, the download and the restart banner stay here, so there is one
        // install path whether the offer arrived at startup or from that button.
        await _dialogs.ShowAboutAsync(new AboutViewModel(
            _portraits,
            characterInfo,
            _services?.GetService<IUpdateService>(),
            _services?.GetService<IUpdateSupportProbe>(),
            ShowUpdateOfferAsync));
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
            await dispatcher.Send(new SetSettingCommand(
                CheckUpdatesOnStartupSettingKey, result.CheckUpdatesOnStartup ? "true" : "false"));
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
            var local = c.HasEsiToken ? "local token" : "no local token";
            var detail = has ? local : $"{local} · missing {requiredScope}";
            return new CharacterPickOption(c.CharacterId, c.Name, detail, Enabled: has && c.HasEsiToken);
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

            // The ESI chip comes from the tracked per-character status, never from the row's default: this rebuild
            // fires after every sign-in and after any registry write, and re-deriving it here is exactly how a
            // re-auth warning used to be wiped off every other character (ET-24). Only when nothing has measured
            // this character yet — the very first paint, before the startup check — fall back to whether a token
            // is stored at all, which is what the list showed before any check ran.
            var hasLocalToken = tokenStore is not null && await tokenStore.LoadAsync(charId) is not null;
            var esiStatus = _tokenStatus?.Get(charId)
                            ?? (hasLocalToken ? TokenStatus.Valid : TokenStatus.NoToken);
            var vm = new CharacterViewModel(c) { EsiTokenStatus = esiStatus };
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

        RefreshServerPairingAlert(); // the fresh links carry their own state; no StateChanged fires for a rebuild

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
        int characterId, Func<ServerLinkViewModel, Task> onDecouple, Func<ServerLinkViewModel, Task>? onViewTrust,
        Func<ServerLinkViewModel, Task>? onRecouple = null)
    {
        var links = new List<ServerLinkViewModel>();
        if (_services is null) return links;

        var sessionStore = _services.GetRequiredService<IClientSessionStore>();
        foreach (var addr in await sessionStore.ListServersForCharacterAsync(characterId))
        {
            var display = _serverRegistry is null ? addr : await _serverRegistry.DisplayNameAsync(addr);
            // This character's own state, not the server roll-up — a link seeded from the roll-up would open green
            // for a character whose session the server no longer has (ET-123).
            var state = _busConnector?.StateFor(addr, characterId) ?? ServerConnectionState.Disconnected;
            links.Add(new ServerLinkViewModel(characterId, addr, display, state, onDecouple, onViewTrust, onRecouple));
        }
        return links;
    }

    /// <summary>The pinned TLS cert fingerprint for a server, shown in the trust dialog.</summary>
    public string? GetServerFingerprint(string serverAddress) =>
        _services?.GetRequiredService<IServerTrustStore>().GetFingerprint(serverAddress);

    /// <summary>The fingerprint that server last presented, whether it matched the pin or not — the second half of
    /// the comparison the certificate banner asks the user to make (ET-95).</summary>
    private string? GetPresentedServerFingerprint(string serverAddress) =>
        _services?.GetRequiredService<GrpcChannelFactory>().PresentedFingerprint(serverAddress);

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

    /// <summary>
    /// <paramref name="restoreAddress"/> couples a server this client is already paired to again — the way back from
    /// a session the server has dropped. The dialog opens with the address and the user's own label already filled
    /// in, because both are stored with the coupling being restored and retyping them is asking for something the
    /// client already knows (ET-123).
    /// <para>Deliberately NOT offered after a refused certificate: there the address is precisely what is in
    /// question, and handing it back pre-filled would walk the user past the fingerprint check (ET-95). The only
    /// caller that passes an address is the link's recouple action, which is gated on
    /// <see cref="ServerLinkViewModel.CanRecouple"/>.</para>
    /// </summary>
    public async Task<bool> RunCoupleAsync(string? restoreAddress = null)
    {
        if (_pairing is null || _dialogs is null) return false;

        CoupleServerResult? prefill = null;
        if (!string.IsNullOrWhiteSpace(restoreAddress))
        {
            // The label only — not the server's own name, which the dialog already falls back to on its own; putting
            // it in the box would turn it into a user label the user never chose.
            var known = _serverRegistry is null ? null : await _serverRegistry.GetAsync(restoreAddress);
            prefill = new CoupleServerResult(restoreAddress, known?.Label);
        }

        var couple = await _dialogs.CoupleServerAsync(ProbeServerNameAsync, prefill);
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

        // The token check talks to EVE SSO and shows a modal; it is the step most likely to fail, and the three
        // steps behind it have nothing to do with tokens. Keep its failure to itself instead of silently skipping
        // the fittings list, the server tabs and the home dashboard (ET-24).
        try
        {
            await CheckTokensOnStartupAsync(); // refresh/flag ESI tokens at startup
        }
        catch (Exception ex)
        {
            ActivityStatus = $"ESI token check failed: {ex.Message}";
        }

        await LoadFittingsAsync();             // global Local fittings list (all characters)
        await RefreshFittingsTabsAsync();      // server tabs for the restored connections
        await Home.RefreshAsync();             // home dashboard: your fleets, latest shared fits, character stats
    }

    /// <summary>
    /// The startup chain, kicked off from the constructor. Wrapped because a bare <c>_ = LoadAsync()</c> loses
    /// whatever it throws into an unobserved task: the window comes up half-loaded with nothing said about it.
    /// </summary>
    private async Task RunStartupResilientAsync()
    {
        try
        {
            await LoadAsync();
        }
        catch (Exception ex)
        {
            ActivityStatus = $"Startup failed: {ex.Message}";
        }
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

    // ── Application updates (ET-32) ─────────────────────────────────────────────────────────────────
    private const string CheckUpdatesOnStartupSettingKey = "updates.check-on-startup";   // default on

    /// <summary>
    /// The restart banner: a package is downloaded and waiting, and stays waiting until it is applied.
    /// </summary>
    [ObservableProperty] private bool _isUpdateReady;

    [ObservableProperty] private string _updateReadyMessage = "";

    /// <summary>
    /// Kicks off the update check the way <see cref="StartSdeUpdateCheck"/> does: after the window is up and off the
    /// startup chain, so a feed that never answers holds nothing up.
    /// </summary>
    public void StartUpdateCheck() => _ = RunUpdateCheckResilientAsync();

    private async Task RunUpdateCheckResilientAsync()
    {
        try
        {
            await CheckForUpdateAsync();
        }
        catch (Exception ex)
        {
            ActivityStatus = $"Update check failed: {ex.Message}";
        }
    }

    private async Task CheckForUpdateAsync()
    {
        if (_services is null || !await IsStartupUpdateCheckEnabledAsync()) return;

        var check = await _services.GetRequiredService<IUpdateService>().CheckAsync();

        if (UpdateNotice.StartupStatus(check, InstalledVersion) is { } status)
            ActivityStatus = status;

        if (UpdateNotice.Classify(check) is UpdateNoticeKind.Available)
            OfferUpdate(check.Value!);
    }

    // Read straight from the store rather than from the loaded Settings collection: this runs off the startup chain,
    // so LoadAsync may not have filled that collection yet.
    private async Task<bool> IsStartupUpdateCheckEnabledAsync()
    {
        using var scope = _services!.CreateScope();
        var settings = await scope.ServiceProvider.GetRequiredService<IDispatcher>().Query(new GetSettingsQuery());

        return settings.FirstOrDefault(s => s.Key == CheckUpdatesOnStartupSettingKey)?.Value != "false";
    }

    // Bottom right and with no expiry, both deliberate: there is no periodic re-check, so this offer is made exactly
    // once per session and a toast that walks away on a timer takes that one chance with it.
    private void OfferUpdate(AppRelease release) =>
        _services?.GetService<IToastService>()?.Show(
            "Update available",
            $"EVE Together v{release.Version} is ready to download. You're on {InstalledVersion}.",
            ToastKind.Information,
            [
                new ToastAction("Later", () => { }),
                new ToastAction("What's new", () => _ = ShowUpdateOfferAsync(release), ToastActionStyle.Affirmative),
            ],
            ToastPosition.BottomRight);

    /// <summary>
    /// Shows the offer and, only if the user accepts it there, fetches the package. Nothing is downloaded before that.
    /// </summary>
    public async Task ShowUpdateOfferAsync(AppRelease release)
    {
        if (_dialogs is null || !await _dialogs.ShowUpdateAvailableAsync(InstalledVersion, release)) return;

        await DownloadUpdateAsync(release);
    }

    private async Task DownloadUpdateAsync(AppRelease release)
    {
        if (_services is null) return;

        ActivityStatus =
            $"Downloading v{release.Version}… the app stays usable, you'll be asked to restart when it's ready.";

        var download = await _services.GetRequiredService<IUpdateService>().DownloadAsync();
        if (!download.IsSuccess)
        {
            ActivityStatus = UpdateNotice.Reason(download);
            return;
        }

        ActivityStatus = $"Update v{release.Version} downloaded — restart to finish updating.";
        UpdateReadyMessage = $"v{release.Version} is ready. Restart to finish updating.";
        IsUpdateReady = true;
    }

    /// <summary>
    /// Applies the downloaded package and comes back on the new build.
    /// </summary>
    [RelayCommand]
    private void RestartForUpdate() => _services?.GetRequiredService<IUpdateService>().ApplyDownloadedUpdateAndRestart();

    /// <summary>
    /// Hides the banner. The package stays on disk but nothing applies it, so this promises nothing beyond quiet.
    /// </summary>
    [RelayCommand]
    private void DismissUpdateReady() => IsUpdateReady = false;

    private static string InstalledVersion => $"v{EveUtils.Shared.App.AppInfo.Version}";

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
