using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Controls.Map;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Fleet;
using EveUtils.Client.Imaging;
using EveUtils.Client.WorldMap;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Enums;
using EveUtils.Shared.Modules.Map.Dtos;
using EveUtils.Shared.Modules.Map.Enums;
using EveUtils.Shared.Modules.Map.Queries;
using EveUtils.Shared.Modules.Settings.Commands;
using EveUtils.Shared.Modules.Settings.Dtos;
using EveUtils.Shared.Modules.Settings.Queries;

namespace EveUtils.Client.ViewModels.Map;

/// <summary>
/// The MAP module (ET-392): New Eden to zoom around in, a wayfinder over the local map, a system search and your own
/// characters where the game log last saw them. The graph comes from <see cref="GetMapGraphQuery"/>, which builds it
/// off the UI thread once per SDE build (ET-298); everything here only reads that immutable snapshot.
///
/// <para>Following (ET-393): the map keeps one of your own characters in view, recentring on every jump, until you
/// move the map yourself — then it pauses until RESUME or a new pick. Nobody is followed by default; the pilot picks
/// who, every time (AGENTS.md: no global active character). The character's trail is read from
/// <see cref="MapTrailRecorder"/> and cut to the window the pilot chose.</para>
///
/// <para>Fleets (ET-394): every member of your active fleets with a current position is counted on a badge per system,
/// and following a fleet keeps all of them framed. Which fleet is again an explicit pick. A position counts as current
/// while it is younger than <see cref="FleetPositionExpiry"/>, except one from your own game log: the log only writes on
/// a jump, so a pilot sitting in one system for an hour is still there. Every other source repeats itself every few
/// seconds while it is live (location metric 1 s, ESI location 6 s, ESI fleet 5 s), so ten minutes of silence means the
/// pilot stopped sharing, logged off or left the in-game fleet — they are left off the badges and out of the frame, and
/// the FOLLOW block says how many.</para>
/// </summary>
public sealed partial class MapViewModel : ObservableObject, IRefreshableModule, IDisposable
{
    /// <summary>Width of the route's security bar: the side panel's inner width.</summary>
    public const double SecurityBarWidth = 258;

    /// <summary>Following never zooms out past system view: the level where a jump is something you can see.</summary>
    public const double FollowMinZoom = 9;

    /// <summary>How long a fleet member's position stays on the map without being confirmed again (see the class remarks).</summary>
    public static readonly TimeSpan FleetPositionExpiry = TimeSpan.FromMinutes(10);

    // A repeat sighting in the same system raises no event, so the badges' ages and the expiry are re-read on a beat.
    private static readonly TimeSpan BadgeRefresh = TimeSpan.FromSeconds(5);

    // How long fleet news is gathered before the servers are asked which fleets you are in.
    private static readonly TimeSpan MembershipWindow = TimeSpan.FromSeconds(1);

    public static IReadOnlyList<int> TrailJumpOptions { get; } = [5, 10, 20, 50];

    public static IReadOnlyList<TrailSinceOption> TrailSinceOptions { get; } =
    [
        new(TrailSince.Last15Minutes, "Last 15 minutes"),
        new(TrailSince.LastHour, "Last hour"),
        new(TrailSince.SinceDowntime, "Since downtime"),
        new(TrailSince.SinceAppStart, "Since app start")
    ];

    private readonly IDispatcher _dispatcher;
    private readonly ICharacterRegistry _registry;
    private readonly IFleetPositionSource _positions;
    private readonly MapTrailRecorder _trails;
    private readonly IMapFleetSource _fleets;
    private readonly TimeProvider _clock;
    private readonly ICharacterPortraitProvider? _portraits;
    private readonly IDialogService? _dialogs;
    private readonly IDisposable _rosterWatch;
    private readonly ITimer _badgeBeat;
    private readonly Dictionary<long, IReadOnlyCollection<int>> _fleetMembers = [];
    private readonly Dictionary<long, int?> _fleetCommanders = [];
    private readonly Dictionary<int, string> _names = [];
    private readonly HashSet<int> _namesAsked = [];
    private IReadOnlyList<Character> _characters = [];
    private (long FleetId, string? ServerAddress)? _pinnedFleet;
    private bool _pinnedFollowOff;
    private bool _isReloading;
    private bool _reloadAgain;
    private bool _membershipStale;
    private Task? _reloadRun;

    public MapViewModel(IDispatcher dispatcher, ICharacterRegistry registry, IFleetPositionSource positions, MapTrailRecorder trails,
        IMapFleetSource fleets, TimeProvider clock, ICharacterPortraitProvider? portraits = null, IDialogService? dialogs = null)
    {
        _dispatcher = dispatcher;
        _registry = registry;
        _positions = positions;
        _trails = trails;
        _fleets = fleets;
        _clock = clock;
        _portraits = portraits;
        _dialogs = dialogs;
        if (dialogs is not null)
            dialogs.MapPresentationChanged += _OnPresentationChanged;
        _registry.RegistryChanged += _OnRegistryChanged;
        _positions.PositionChanged += _OnPositionChanged;
        _rosterWatch = fleets.WatchRosters(_OnRosterChanged);
        _badgeBeat = clock.CreateTimer(_ => Avalonia.Threading.Dispatcher.UIThread.Post(_OnBadgeBeat), null, BadgeRefresh, BadgeRefresh);
    }

    /// <summary>What the badges' "12s ago" is measured against.</summary>
    public TimeProvider Clock => _clock;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountsText))]
    private MapGraphDto? _graph;

    /// <summary>Why there is no map, or null once it shows.</summary>
    [ObservableProperty] private string? _statusMessage = "Loading the map…";

    [ObservableProperty] private IReadOnlyList<string> _systemNames = [];
    [ObservableProperty] private IReadOnlyList<MapMarker> _markers = [];
    [ObservableProperty] private MapFocusRequest? _focusRequest;
    [ObservableProperty] private bool _isLegendOpen;

    // ── Side panel (ET-397) ──────────────────────────────────────────────────────────────────────

    // The MAP tab and the popped-out window each remember whether their side panel is folded away.
    public const string PanelCollapsedInTabSettingKey = "ui.map.panel-collapsed.tab";

    public const string PanelCollapsedInWindowSettingKey = "ui.map.panel-collapsed.window";

    public const double ExpandedPanelWidth = 286;

    /// <summary>What is left of the side panel when it is folded away: a strip with the button to unfold it.</summary>
    public const double CollapsedPanelWidth = 28;

    private bool _panelCollapsedInTab;
    private bool _panelCollapsedInWindow;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PanelToggleTip), nameof(PanelWidth))]
    private bool _isPanelCollapsed;

    public string PanelToggleTip => IsPanelCollapsed ? "Show panel" : "Hide panel";

    public double PanelWidth => IsPanelCollapsed ? CollapsedPanelWidth : ExpandedPanelWidth;

    [RelayCommand]
    private async Task TogglePanelAsync()
    {
        bool collapsed = !IsPanelCollapsed;
        if (IsPoppedOut)
            _panelCollapsedInWindow = collapsed;
        else
            _panelCollapsedInTab = collapsed;
        IsPanelCollapsed = collapsed;
        await _dispatcher.Send(new SetSettingCommand(_PanelSettingKey(), collapsed ? "true" : "false"));
    }

    private string _PanelSettingKey() => IsPoppedOut ? PanelCollapsedInWindowSettingKey : PanelCollapsedInTabSettingKey;

    private async Task _LoadPanelStateAsync()
    {
        IReadOnlyList<SettingDto> settings = await _dispatcher.Query(new GetSettingsQuery());
        _panelCollapsedInTab = settings.Any(setting => setting.Key == PanelCollapsedInTabSettingKey && setting.Value == "true");
        _panelCollapsedInWindow = settings.Any(setting => setting.Key == PanelCollapsedInWindowSettingKey && setting.Value == "true");
        _ShowPanelStateOfThisView();
    }

    private void _ShowPanelStateOfThisView() => IsPanelCollapsed = IsPoppedOut ? _panelCollapsedInWindow : _panelCollapsedInTab;

    // ── Pop-out (ET-396) ─────────────────────────────────────────────────────────────────────────

    /// <summary>The map is in a window of its own and the MAP tab holds a placeholder.</summary>
    public bool IsPoppedOut => _dialogs?.IsMapPoppedOut == true;

    /// <summary>POP OUT is offered where the map is a tab and not out already; a floating map is a window to begin with.</summary>
    public bool CanPopOut => _dialogs?.CanPopOutMap == true;

    [RelayCommand]
    private void PopOut() => _dialogs?.PopOutMap();

    [RelayCommand]
    private void PutBack() => _dialogs?.PutBackMap();

    [RelayCommand]
    private void ShowWindow() => _dialogs?.ShowMapWindow();

    private void _OnPresentationChanged()
    {
        OnPropertyChanged(nameof(IsPoppedOut));
        OnPropertyChanged(nameof(CanPopOut));
        _ShowPanelStateOfThisView();
    }

    // ── Follow ───────────────────────────────────────────────────────────────────────────────────

    public ObservableCollection<MapCharacterRowViewModel> Characters { get; } = [];

    public ObservableCollection<MapFleetRowViewModel> Fleets { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFollowOff), nameof(IsFollowCharacter), nameof(IsFollowFleet), nameof(NeedsCharacterPick), nameof(NeedsFleetPick))]
    private MapFollowMode _followMode = MapFollowMode.Off;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFollowing), nameof(IsFollowingCharacter), nameof(NeedsCharacterPick), nameof(FollowChipText),
        nameof(FollowStatusText), nameof(TrailResetText))]
    private MapCharacterRowViewModel? _followedCharacter;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFollowing), nameof(IsFollowingFleet), nameof(NeedsFleetPick), nameof(FollowChipText), nameof(FollowStatusText),
        nameof(FleetChipText))]
    private MapFleetRowViewModel? _followedFleet;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FollowChipText), nameof(FollowStatusText), nameof(FleetChipText))]
    private bool _isFollowPaused;

    /// <summary>The system of the followed character, ringed on the map; -1 when there is none or the map does not hold it.</summary>
    [ObservableProperty] private int _followIndex = -1;

    /// <summary>Members of your active fleets with a current position, one badge per system.</summary>
    [ObservableProperty] private IReadOnlyList<MapFleetBadge> _fleetBadges = [];

    /// <summary>The followed fleet in a line: how many members, in how many systems.</summary>
    [ObservableProperty] private string _fleetSpreadText = string.Empty;

    /// <summary>How many of the followed fleet's members have no current position; empty when all do.</summary>
    [ObservableProperty] private string _fleetUnplacedText = string.Empty;

    /// <summary>The followed fleet's occupied systems, the one holding its commander first.</summary>
    [ObservableProperty] private IReadOnlyList<MapFleetSystemChip> _fleetSystemChips = [];

    /// <summary>Why the last "show me this member" went nowhere; empty otherwise.</summary>
    /// <summary>"FC position unknown" while the followed fleet's commander has no current position on the map (or the
    /// fleet names none); empty when the star marks them.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFleetCommanderNotice))]
    private string _fleetCommanderText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMemberNotice))]
    private string _memberNoticeText = string.Empty;

    public bool HasMemberNotice => MemberNoticeText.Length > 0;

    public bool IsFollowOff => FollowMode == MapFollowMode.Off;
    public bool IsFollowCharacter => FollowMode == MapFollowMode.Character;
    public bool IsFollowFleet => FollowMode == MapFollowMode.Fleet;
    public bool IsFollowingCharacter => FollowMode == MapFollowMode.Character && FollowedCharacter is not null;
    public bool IsFollowingFleet => FollowMode == MapFollowMode.Fleet && FollowedFleet is not null;
    public bool IsFollowing => IsFollowingCharacter || IsFollowingFleet;
    public bool NeedsCharacterPick => FollowMode == MapFollowMode.Character && FollowedCharacter is null;
    public bool NeedsFleetPick => FollowMode == MapFollowMode.Fleet && FollowedFleet is null;
    public bool HasCharacters => Characters.Count > 0;
    public bool HasFleets => Fleets.Count > 0;
    public bool HasFleetUnplaced => FleetUnplacedText.Length > 0;
    public bool HasFleetCommanderNotice => FleetCommanderText.Length > 0;

    public string FollowChipText => _FollowedName() is not { } followed
        ? string.Empty
        : IsFollowPaused ? "PAUSED · you moved the map" : "FOLLOWING " + followed.ToUpperInvariant();

    /// <summary>The fleet card's chip, short enough for a 440 px card: the fleet is named in the card's own header.</summary>
    public string FleetChipText => IsFollowingFleet ? IsFollowPaused ? "PAUSED" : "FOLLOWING FLEET" : "NOT FOLLOWING";

    /// <summary>The status bar's line; empty when the map follows nobody.</summary>
    public string FollowStatusText => _FollowedName() is { } followed
        ? IsFollowPaused ? $"Map: following {followed} (paused)" : $"Map: following {followed}"
        : string.Empty;

    partial void OnFleetUnplacedTextChanged(string value) => OnPropertyChanged(nameof(HasFleetUnplaced));

    // ── Trail ────────────────────────────────────────────────────────────────────────────────────

    [ObservableProperty] private bool _isTrailOn = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTrailByJumps), nameof(IsTrailBySince))]
    private TrailWindow _trailWindow = TrailWindow.LastJumps;

    [ObservableProperty] private int _trailJumps = 10;
    [ObservableProperty] private TrailSince _trailSince = TrailSince.LastHour;

    /// <summary>The followed character's trail as the map draws it; null when off or nobody is followed.</summary>
    [ObservableProperty] private IReadOnlyList<MapTrailStep>? _trail;

    public bool IsTrailByJumps => TrailWindow == TrailWindow.LastJumps;
    public bool IsTrailBySince => TrailWindow == TrailWindow.Since;

    public string TrailResetText => FollowedCharacter is { } followed && _trails.TrailOf(followed.CharacterId).ResetAt is { } resetAt
        ? "reset at " + resetAt.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)
        : string.Empty;

    // ── Route ────────────────────────────────────────────────────────────────────────────────────

    [ObservableProperty] private string? _fromText;
    [ObservableProperty] private string? _toText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsShortest), nameof(IsSafer), nameof(IsLessSecure))]
    private RoutePreference _preference = RoutePreference.Shortest;

    [ObservableProperty] private bool _avoidLowsec;
    [ObservableProperty] private bool _avoidNullsec;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRoute), nameof(RouteSteps), nameof(RouteIndexes), nameof(RouteJumpsText), nameof(RouteEndsText),
        nameof(HighsecText), nameof(LowsecText), nameof(NullsecText),
        nameof(HighsecBarWidth), nameof(LowsecBarWidth), nameof(NullsecBarWidth))]
    private RouteDto? _route;

    [ObservableProperty] private string? _routeMessage;

    // ── Find ─────────────────────────────────────────────────────────────────────────────────────

    [ObservableProperty] private string? _findText;
    [ObservableProperty] private string? _findMessage;

    // ── Selection ────────────────────────────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedSystem), nameof(HasSelection), nameof(SelectedName), nameof(SelectedSecurityText),
        nameof(SelectedSecurityBrush), nameof(SelectedConstellationText), nameof(SelectedRegionText), nameof(SelectedRegionBrush),
        nameof(SelectedGatesText), nameof(SelectedHereText), nameof(HasSelectedHere))]
    private int _selectedIndex = -1;

    public string CountsText => Graph is { } graph
        ? string.Create(CultureInfo.InvariantCulture, $"New Eden · {graph.Systems.Count:N0} systems · {graph.Jumps.Count:N0} jumps")
        : "New Eden";

    public bool IsShortest => Preference == RoutePreference.Shortest;
    public bool IsSafer => Preference == RoutePreference.Safer;
    public bool IsLessSecure => Preference == RoutePreference.LessSecure;

    public bool HasRoute => Route is not null;

    public IReadOnlyList<int>? RouteIndexes => Route?.Steps.Select(step => step.SystemIndex).ToList();

    public IReadOnlyList<MapRouteStepViewModel> RouteSteps =>
        Route?.Steps.Select((step, number) => new MapRouteStepViewModel(number, step)).ToList() ?? [];

    public string RouteJumpsText => Route?.Jumps.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    public string RouteEndsText => Route is { } route
        ? $"{(route.Jumps == 1 ? "jump" : "jumps")} · {route.Steps[0].Name} → {route.Steps[^1].Name}"
        : string.Empty;

    public string HighsecText => $"{Route?.HighsecJumps ?? 0} highsec";
    public string LowsecText => $"{Route?.LowsecJumps ?? 0} lowsec";
    public string NullsecText => $"{Route?.NullsecJumps ?? 0} nullsec";

    public double HighsecBarWidth => _BarWidth(Route?.HighsecJumps);
    public double LowsecBarWidth => _BarWidth(Route?.LowsecJumps);
    public double NullsecBarWidth => _BarWidth(Route?.NullsecJumps);

    public MapSystemDto? SelectedSystem =>
        Graph is { } graph && SelectedIndex >= 0 && SelectedIndex < graph.Systems.Count ? graph.Systems[SelectedIndex] : null;

    public bool HasSelection => SelectedSystem is not null;
    public string SelectedName => SelectedSystem?.Name ?? string.Empty;
    public string SelectedSecurityText => SelectedSystem?.DisplaySecurity.ToString("0.0", CultureInfo.InvariantCulture) ?? string.Empty;
    public IBrush SelectedSecurityBrush => MapPalette.SecurityBrush(SelectedSystem?.DisplaySecurity ?? 0);

    public string SelectedConstellationText =>
        SelectedSystem is { } system && Graph is { } graph ? graph.Constellations[system.ConstellationIndex].Name + " · " : string.Empty;

    public string SelectedRegionText => SelectedRegion is { } region
        ? region.FactionName is { } faction ? $"{region.Name} · {faction}" : region.Name
        : string.Empty;

    public IBrush SelectedRegionBrush => SelectedRegion is { } region
        ? new SolidColorBrush(MapPalette.Regions[region.ColourIndex % MapPalette.Regions.Count])
        : MapPalette.TextBrush;

    public string SelectedGatesText
    {
        get
        {
            if (SelectedSystem is not { } system || Graph is not { } graph)
                return string.Empty;
            int gates = graph.NeighboursOf(system.Index).Length;
            return gates == 1 ? "1 stargate" : $"{gates} stargates";
        }
    }

    public string SelectedHereText
    {
        get
        {
            List<string> here = Markers.Where(marker => marker.SystemIndex == SelectedIndex).Select(marker => marker.Label).ToList();
            return here.Count == 0 ? string.Empty : "Here: " + string.Join(", ", here);
        }
    }

    public bool HasSelectedHere => SelectedHereText.Length > 0;

    private MapRegionDto? SelectedRegion => SelectedSystem is { } system && Graph is { } graph ? graph.Regions[system.RegionIndex] : null;

    /// <summary>Reads the map; a later call only swaps it in when the SDE build changed, so an open map keeps its view.</summary>
    public async Task LoadAsync()
    {
        await _LoadPanelStateAsync();
        _characters = await _registry.GetAllAsync();
        _SyncCharacterRows();
        Result<MapGraphDto> result = await _dispatcher.Query(new GetMapGraphQuery());
        if (!result.IsSuccess || result.Value is not { } graph)
        {
            StatusMessage = result.Messages.FirstOrDefault()?.Text ?? "The map could not be read.";
            _RefreshPositions();
            return;
        }

        StatusMessage = null;
        if (Graph?.BuildNumber != graph.BuildNumber)
        {
            SelectedIndex = -1;
            Route = null;
            Graph = graph;
            SystemNames = graph.Systems.Select(system => system.Name).Order(StringComparer.OrdinalIgnoreCase).ToList();
        }
        await _ReloadFleetsAsync();
    }

    public void RefreshModule() => _ = LoadAsync();

    [RelayCommand]
    private void SetPreference(RoutePreference preference) => Preference = preference;

    partial void OnPreferenceChanged(RoutePreference value) => _ReplanShownRoute();
    partial void OnAvoidLowsecChanged(bool value) => _ReplanShownRoute();
    partial void OnAvoidNullsecChanged(bool value) => _ReplanShownRoute();

    [RelayCommand]
    private async Task PlanRouteAsync()
    {
        if (Graph is not { } graph)
            return;

        PauseFollow();
        MapSystemDto? from = graph.FindByName(FromText), to = graph.FindByName(ToText);
        if (from is null || to is null)
        {
            Route = null;
            RouteMessage = string.IsNullOrWhiteSpace(FromText) || string.IsNullOrWhiteSpace(ToText)
                ? "Pick where the route starts and where it goes."
                : "Unknown system name.";
            return;
        }

        FromText = from.Name;
        ToText = to.Name;
        var avoid = new HashSet<SecurityBand>();
        if (AvoidLowsec)
            avoid.Add(SecurityBand.Low);
        if (AvoidNullsec)
            avoid.Add(SecurityBand.Null);

        Result<RouteDto> result = await _dispatcher.Query(new PlanRouteQuery(from.SolarSystemId, to.SolarSystemId, Preference, avoid));
        Route = result.IsSuccess ? result.Value : null;
        RouteMessage = result.IsSuccess ? null : result.Messages.FirstOrDefault()?.Text;
        if (RouteIndexes is { } indexes)
            FocusRequest = new MapFocusRequest(indexes);
    }

    [RelayCommand]
    private void ClearRoute()
    {
        Route = null;
        RouteMessage = null;
    }

    [RelayCommand]
    private void SwapRoute() => (FromText, ToText) = (ToText, FromText);

    [RelayCommand]
    private void ShowWholeRoute()
    {
        PauseFollow();
        if (RouteIndexes is { } indexes)
            FocusRequest = new MapFocusRequest(indexes);
    }

    [RelayCommand]
    private void ShowStep(MapRouteStepViewModel step) => _SelectAndShow(step.Step.SystemIndex);

    [RelayCommand]
    private void Find()
    {
        MapSystemDto? system = Graph?.FindByName(FindText);
        FindMessage = system is null && !string.IsNullOrWhiteSpace(FindText) ? "No system by that name." : null;
        if (system is null)
            return;
        FindText = system.Name;
        _SelectAndShow(system.Index);
    }

    [RelayCommand]
    private Task RouteFromSelectedAsync() => _RouteWithSelectedAsync(asStart: true);

    [RelayCommand]
    private Task RouteToSelectedAsync() => _RouteWithSelectedAsync(asStart: false);

    [RelayCommand]
    private void ClearSelection() => SelectedIndex = -1;

    [RelayCommand]
    private void ToggleLegend() => IsLegendOpen = !IsLegendOpen;

    [RelayCommand]
    private void SetFollowMode(MapFollowMode mode) => FollowMode = mode;

    partial void OnFollowModeChanged(MapFollowMode value)
    {
        if (value != MapFollowMode.Character)
            _StopFollowingCharacter();
        if (value != MapFollowMode.Fleet)
            _StopFollowingFleet();
        else if (!_isReloading)
            _ = _ReloadFleetsAsync();
    }

    // ── Pinned fleet (the fleet card) ───────────────────────────────────────────────────────────

    /// <summary>Ties this map to one fleet, as the card in Fleet metrics is: it follows that fleet as soon as it is one of
    /// yours — now, or when it starts — until the pilot turns following off with <see cref="ToggleFleetFollowCommand"/>.</summary>
    public async Task PinFleetAsync(long fleetId, string? serverAddress)
    {
        _pinnedFleet = (fleetId, serverAddress);
        _pinnedFollowOff = false;
        await LoadAsync();
    }

    /// <summary>Whether the pinned fleet is one of your active fleets right now; false once it ended.</summary>
    public bool IsPinnedFleetActive => _PinnedRow() is not null;

    /// <summary>The crosshair: follows the pinned fleet — or, when it is followed but paused, comes back to it — and stops
    /// following when it is being followed.</summary>
    [RelayCommand]
    private void ToggleFleetFollow()
    {
        if (IsFollowingFleet && !IsFollowPaused)
        {
            _pinnedFollowOff = true;
            FollowMode = MapFollowMode.Off;
        }
        else if (IsFollowingFleet)
            ResumeFollow();
        else if (_PinnedRow() is { } pinned)
        {
            _pinnedFollowOff = false;
            FollowFleet(pinned);
        }
    }

    /// <summary>Opens on this fleet: waits for the fleets you are in to be read again — the one asked for may have started
    /// a moment ago — and follows it. A fleet that is not one of yours leaves the map as it was.</summary>
    public async Task FollowFleetByIdAsync(long fleetId, string? serverAddress)
    {
        await _ReloadFleetsAsync(refreshMembership: true);
        if (_RowOf(fleetId, serverAddress) is { } row)
            FollowFleet(row);
    }

    /// <summary>Flies to where this member is and stops following: the pilot looked at one person, not at the fleet. Says
    /// so when the member has no position from the last ten minutes.</summary>
    public void ShowMember(int characterId, string name)
    {
        FleetPositionDto? position = _positions.GetPositions().FirstOrDefault(sighting => sighting.CharacterId == characterId);
        if (position is null || !_IsCurrent(position) || _SystemOf(position) is not { } system)
        {
            MemberNoticeText = $"{name} has no position from the last {FleetPositionExpiry.TotalMinutes:0} minutes.";
            return;
        }

        MemberNoticeText = string.Empty;
        PauseFollow();
        FocusRequest = new MapFocusRequest([system.Index], FollowMinZoom);
    }

    private MapFleetRowViewModel? _PinnedRow() => _pinnedFleet is { } pinned ? _RowOf(pinned.FleetId, pinned.ServerAddress) : null;

    private MapFleetRowViewModel? _RowOf(long fleetId, string? serverAddress) =>
        Fleets.FirstOrDefault(row => row.Fleet.FleetId == fleetId && (serverAddress is null || row.Fleet.ServerAddress == serverAddress));

    /// <summary>Follows this character from now on — an explicit pick, and it resumes a paused map.</summary>
    [RelayCommand]
    private void FollowCharacter(MapCharacterRowViewModel character)
    {
        FollowMode = MapFollowMode.Character;
        FollowedCharacter = character;
        IsFollowPaused = false;
        foreach (MapCharacterRowViewModel row in Characters)
            row.IsFollowed = ReferenceEquals(row, character);
        _RefreshPositions();
        _FocusFollowed();
    }

    /// <summary>Keeps every member of this fleet in view from now on — an explicit pick, and it resumes a paused map.</summary>
    [RelayCommand]
    private void FollowFleet(MapFleetRowViewModel fleet)
    {
        FollowMode = MapFollowMode.Fleet;
        FollowedFleet = fleet;
        IsFollowPaused = false;
        foreach (MapFleetRowViewModel row in Fleets)
            row.IsFollowed = ReferenceEquals(row, fleet);
        _RefreshPositions();
        _FocusFollowed();
    }

    [RelayCommand]
    private void ResumeFollow()
    {
        IsFollowPaused = false;
        _FocusFollowed();
    }

    /// <summary>The pilot moved the map, searched or planned a route: the map stops recentring until RESUME.</summary>
    public void PauseFollow()
    {
        if (IsFollowing)
            IsFollowPaused = true;
    }

    [RelayCommand]
    private void SetTrailWindow(TrailWindow window)
    {
        TrailWindow = window;
        _RefreshTrail();
    }

    partial void OnIsTrailOnChanged(bool value) => _RefreshTrail();
    partial void OnTrailJumpsChanged(int value) => _RefreshTrail();
    partial void OnTrailSinceChanged(TrailSince value) => _RefreshTrail();

    /// <summary>Forgets where the followed character has been; the trail starts over from the current system.</summary>
    [RelayCommand]
    private void ResetTrail()
    {
        if (FollowedCharacter is not { } followed)
            return;
        _trails.ResetTrail(followed.CharacterId);
        OnPropertyChanged(nameof(TrailResetText));
        _RefreshTrail();
    }

    public void Dispose()
    {
        if (_dialogs is not null)
            _dialogs.MapPresentationChanged -= _OnPresentationChanged;
        _registry.RegistryChanged -= _OnRegistryChanged;
        _positions.PositionChanged -= _OnPositionChanged;
        _rosterWatch.Dispose();
        _badgeBeat.Dispose();
        _StopFollowingCharacter();
        _StopFollowingFleet();
    }

    private void _StopFollowingCharacter()
    {
        FollowedCharacter = null;
        if (FollowedFleet is null)
            IsFollowPaused = false;
        foreach (MapCharacterRowViewModel row in Characters)
            row.IsFollowed = false;
        FollowIndex = -1;
        Trail = null;
    }

    private void _StopFollowingFleet()
    {
        FollowedFleet = null;
        if (FollowedCharacter is null)
            IsFollowPaused = false;
        foreach (MapFleetRowViewModel row in Fleets)
            row.IsFollowed = false;
        FleetSpreadText = string.Empty;
        FleetUnplacedText = string.Empty;
        FleetCommanderText = string.Empty;
        FleetSystemChips = [];
    }

    private void _FocusFollowed()
    {
        if (IsFollowingFleet)
        {
            List<int> systems = _FollowedFleetSystems();
            if (systems.Count > 0)
                FocusRequest = new MapFocusRequest(systems, framing: MapFraming.Fleet);
        }
        else if (FollowIndex >= 0)
            FocusRequest = new MapFocusRequest([FollowIndex], FollowMinZoom);
    }

    private string? _FollowedName() =>
        IsFollowingCharacter ? FollowedCharacter?.Name
        : IsFollowingFleet ? "fleet " + FollowedFleet?.Name
        : null;

    private async Task _RouteWithSelectedAsync(bool asStart)
    {
        if (SelectedSystem is not { } system)
            return;
        if (asStart)
            FromText = system.Name;
        else
            ToText = system.Name;
        if (!string.IsNullOrWhiteSpace(FromText) && !string.IsNullOrWhiteSpace(ToText))
            await PlanRouteAsync();
        else
            RouteMessage = asStart ? "Now pick where the route goes." : "Now pick where the route starts.";
    }

    private void _ReplanShownRoute()
    {
        if (Route is not null)
            _ = PlanRouteAsync();
    }

    private void _SelectAndShow(int systemIndex)
    {
        PauseFollow();
        SelectedIndex = systemIndex;
        FocusRequest = new MapFocusRequest([systemIndex]);
    }

    private double _BarWidth(int? jumps) => jumps is { } count ? SecurityBarWidth * count / Math.Max(1, Route?.Jumps ?? 0) : 0;

    private void _OnRegistryChanged() => Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = LoadAsync());

    // Raised on whichever thread saw the jump; the view model only ever touches its state on the UI thread (ET-298).
    private void _OnPositionChanged(FleetPositionDto position) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() => _OnPositionOnUiThread(position));

    private void _OnPositionOnUiThread(FleetPositionDto position)
    {
        _RefreshPositions();
        if (IsFollowPaused)
            return;
        if ((IsFollowingCharacter && FollowedCharacter?.CharacterId == position.CharacterId)
            || (IsFollowingFleet && FollowedFleet is { } fleet && _MembersOf(fleet).Contains(position.CharacterId)))
            _FocusFollowed();
    }

    private void _OnBadgeBeat()
    {
        if (_fleetMembers.Count > 0)
            _RefreshFleetBadges(_positions.GetPositions().ToDictionary(position => position.CharacterId));
    }

    // Any fleet's news, not only that of a fleet already listed: one that starts, ends or is joined is not (or no longer)
    // in the list. The fleet module's change signal (RosterReloaded) is what says a fleet's state or membership moved, so
    // it also re-reads the set of fleets you are in first. The other kinds are screens announcing a pilot they just
    // removed or changed: the participation set already dropped a removed pilot at once (ET-49), and a sweep asked
    // now could hand them straight back.
    private void _OnRosterChanged(FleetRosterChange change) =>
        _ = _ReloadFleetsAsync(refreshMembership: change.Kind == FleetRosterChangeKind.RosterReloaded);

    // Reloads never overlap: one that is asked for while another runs makes it go round once more, and everyone waiting
    // gets the run that started last.
    private Task _ReloadFleetsAsync(bool refreshMembership = false)
    {
        _membershipStale |= refreshMembership;
        if (_isReloading)
        {
            _reloadAgain = true;
            return _reloadRun ?? Task.CompletedTask;
        }

        _isReloading = true;
        _reloadRun = _RunReloadsAsync();
        return _reloadRun;
    }

    private async Task _RunReloadsAsync()
    {
        try
        {
            do
            {
                _reloadAgain = false;
                if (_membershipStale)
                {
                    // A busy fleet raises the signal for every move and role change; one sweep of the servers covers the burst.
                    await Task.Delay(MembershipWindow, _clock);
                    _membershipStale = false;
                    _reloadAgain = false;
                    await _fleets.RefreshActiveFleetsAsync();
                }

                await _ApplyFleetsAsync();
            } while (_reloadAgain);
        }
        finally
        {
            _isReloading = false;
            _reloadRun = null;
        }
    }

    // The active fleets and their members, re-read when the map loads, FLEET is chosen or a roster moves. A fleet that
    // is no longer active drops out of the list, and following it stops.
    private async Task _ApplyFleetsAsync()
    {
        IReadOnlyList<MapFleetChoice> choices = _fleets.ActiveFleets();
        var members = new Dictionary<long, IReadOnlyCollection<int>>();
        var commanders = new Dictionary<long, int?>();
        foreach (MapFleetChoice choice in choices)
        {
            // The commander is a member whether or not the roster lists them: the map counts, frames and marks them.
            int? commander = _fleets.CommanderOf(choice);
            commanders[choice.FleetId] = commander;
            IReadOnlyCollection<int> ids = await _fleets.MembersOfAsync(choice);
            members[choice.FleetId] = commander is { } id && !ids.Contains(id) ? [.. ids, id] : ids;
        }

        Dictionary<MapFleetChoice, MapFleetRowViewModel> known = Fleets.ToDictionary(row => row.Fleet);
        Fleets.Clear();
        foreach (MapFleetChoice choice in choices)
            Fleets.Add(known.GetValueOrDefault(choice) ?? new MapFleetRowViewModel(choice));
        OnPropertyChanged(nameof(HasFleets));
        if (FollowedFleet is { } followed && !Fleets.Contains(followed))
            _StopFollowingFleet();

        _fleetMembers.Clear();
        foreach ((long fleetId, IReadOnlyCollection<int> ids) in members)
            _fleetMembers[fleetId] = ids;
        _fleetCommanders.Clear();
        foreach ((long fleetId, int? commander) in commanders)
            _fleetCommanders[fleetId] = commander;
        foreach (MapFleetRowViewModel row in Fleets)
            row.MembersText = _MembersOf(row).Count == 1 ? "1 member" : $"{_MembersOf(row).Count} members";

        OnPropertyChanged(nameof(IsPinnedFleetActive));
        if (_PinnedRow() is { } pinned && !_pinnedFollowOff && !IsFollowingFleet)
            FollowFleet(pinned);
        _RefreshPositions();
        if (IsFollowingFleet && !IsFollowPaused)
            _FocusFollowed();
    }

    private IReadOnlyCollection<int> _MembersOf(MapFleetRowViewModel fleet) =>
        _fleetMembers.GetValueOrDefault(fleet.Fleet.FleetId) ?? [];

    // Gamelog positions never expire: the log only writes on a jump (see the class remarks).
    private bool _IsCurrent(FleetPositionDto position) =>
        position.Source == PositionSource.Gamelog || _clock.GetUtcNow() - position.ObservedAt <= FleetPositionExpiry;

    private List<int> _FollowedFleetSystems()
    {
        if (FollowedFleet is not { } fleet)
            return [];
        Dictionary<int, FleetPositionDto> positions = _positions.GetPositions().ToDictionary(position => position.CharacterId);
        return
        [
            .. _MembersOf(fleet)
                .Select(id => positions.GetValueOrDefault(id))
                .Where(position => position is not null && _IsCurrent(position))
                .Select(_SystemOf)
                .OfType<MapSystemDto>()
                .Select(system => system.Index)
                .Distinct()
        ];
    }

    // One row per own character that has an ESI id: positions are keyed by it.
    private void _SyncCharacterRows()
    {
        Dictionary<int, MapCharacterRowViewModel> known = Characters.ToDictionary(row => row.CharacterId);
        var rows = new List<MapCharacterRowViewModel>();
        foreach (Character character in _characters.Where(character => character.EsiCharacterId is > 0))
        {
            int id = character.EsiCharacterId ?? 0;
            if (!known.TryGetValue(id, out MapCharacterRowViewModel? row))
            {
                row = new MapCharacterRowViewModel(character.Name, id) { IsFollowed = FollowedCharacter?.CharacterId == id };
                if (_portraits is not null)
                    _ = row.LoadPortraitAsync(_portraits);
            }
            rows.Add(row);
        }

        Characters.Clear();
        foreach (MapCharacterRowViewModel row in rows)
            Characters.Add(row);
        OnPropertyChanged(nameof(HasCharacters));
    }

    // Own characters where the merged positions last put them, the followed one ringed, and its trail.
    private void _RefreshPositions()
    {
        Dictionary<int, FleetPositionDto> positions = _positions.GetPositions().ToDictionary(position => position.CharacterId);
        var markers = new List<MapMarker>();
        foreach (MapCharacterRowViewModel row in Characters)
        {
            FleetPositionDto? position = positions.GetValueOrDefault(row.CharacterId);
            MapSystemDto? system = _SystemOf(position);
            row.ShowPosition(position, system);
            if (system is not null)
                markers.Add(new MapMarker(system.Index, row.Name));
        }

        Markers = markers;
        OnPropertyChanged(nameof(SelectedHereText));
        OnPropertyChanged(nameof(HasSelectedHere));
        FollowIndex = FollowedCharacter is { } followed ? _SystemOf(positions.GetValueOrDefault(followed.CharacterId))?.Index ?? -1 : -1;
        _RefreshTrail();
        _RefreshFleetBadges(positions);
    }

    // One badge per system for every member of your active fleets whose position is current; the followed fleet's
    // spread and who of it the map cannot place go to the FOLLOW block.
    private void _RefreshFleetBadges(Dictionary<int, FleetPositionDto> positions)
    {
        var placed = new List<(int SystemIndex, int CharacterId, FleetPositionDto Position)>();
        foreach (int id in _fleetMembers.Values.SelectMany(ids => ids).Distinct())
            if (positions.GetValueOrDefault(id) is { } position && _IsCurrent(position) && _SystemOf(position) is { } system)
                placed.Add((system.Index, id, position));

        HashSet<int> commanders = [.. _fleetCommanders.Values.OfType<int>()];
        FleetBadges =
        [
            .. placed.GroupBy(member => member.SystemIndex)
                .Select(here => new MapFleetBadge(here.Key,
                    [.. here.Select(member => new MapFleetSighting(_NameOf(member.CharacterId, member.Position), member.Position.ObservedAt,
                        commanders.Contains(member.CharacterId)))]))
        ];

        if (FollowedFleet is not { } fleet)
            return;
        int? commander = _fleetCommanders.GetValueOrDefault(fleet.Fleet.FleetId);
        FleetCommanderText = commander is { } commanderId && placed.Any(member => member.CharacterId == commanderId) ? string.Empty : "FC position unknown";
        IReadOnlyCollection<int> members = _MembersOf(fleet);
        List<(int SystemIndex, int CharacterId, FleetPositionDto Position)> inFleet = [.. placed.Where(member => members.Contains(member.CharacterId))];
        int systems = inFleet.Select(member => member.SystemIndex).Distinct().Count();
        FleetSpreadText = $"{_Count(inFleet.Count, "member")} in {_Count(systems, "system")}";
        FleetSystemChips = _SystemChipsOf(inFleet.Select(member => (member.SystemIndex, member.CharacterId)), _fleets.CommanderOf(fleet.Fleet));
        int unplaced = members.Count - inFleet.Count;
        FleetUnplacedText = unplaced == 0
            ? string.Empty
            : $"{_Count(unplaced, "member")} without a position from the last {FleetPositionExpiry.TotalMinutes:0} minutes";
    }

    private List<MapFleetSystemChip> _SystemChipsOf(IEnumerable<(int SystemIndex, int CharacterId)> placed, int? commanderCharacterId)
    {
        if (Graph is not { } graph)
            return [];
        return
        [
            .. placed.GroupBy(member => member.SystemIndex)
                .Select(here => new MapFleetSystemChip(here.Key, graph.Systems[here.Key].Name, here.Count(),
                    HasCommander: commanderCharacterId is { } commander && here.Any(member => member.CharacterId == commander)))
                .OrderByDescending(chip => chip.HasCommander)
                .ThenByDescending(chip => chip.Members)
                .ThenBy(chip => chip.SystemName, StringComparer.OrdinalIgnoreCase)
        ];
    }

    private static string _Count(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    // A position from the game log or ESI location carries the name; the rest are looked up once — your own registry,
    // then public ESI — and the badges redrawn when the answer arrives.
    private string _NameOf(int characterId, FleetPositionDto position)
    {
        if (position.Name is { Length: > 0 } name)
            return name;
        if (_characters.FirstOrDefault(character => character.EsiCharacterId == characterId) is { } own)
            return own.Name;
        if (_names.TryGetValue(characterId, out string? known))
            return known;
        if (_namesAsked.Add(characterId))
            _ = _LookUpNameAsync(characterId);
        return "Unknown pilot";
    }

    private async Task _LookUpNameAsync(int characterId)
    {
        if (await _fleets.NameOfAsync(characterId) is not { } name)
            return;
        _names[characterId] = name;
        // Posted, not run: a cached answer arrives while the badges that asked for it are still being built.
        Avalonia.Threading.Dispatcher.UIThread.Post(_RefreshPositions);
    }

    private MapSystemDto? _SystemOf(FleetPositionDto? position) =>
        position is not null && Graph is { } graph && graph.TryGetIndex(position.SolarSystemId, out int index) ? graph.Systems[index] : null;

    private void _RefreshTrail()
    {
        if (!IsTrailOn || FollowedCharacter is not { } followed || Graph is not { } graph)
        {
            Trail = null;
            return;
        }

        MapTrail trail = _trails.TrailOf(followed.CharacterId);
        DateTimeOffset now = _clock.GetUtcNow();
        IReadOnlyList<MapTrailPoint> points = TrailWindow == TrailWindow.LastJumps
            ? trail.LastJumps(TrailJumps)
            : trail.Since(MapTrail.CutoffFor(TrailSince, now, _trails.StartedAt));

        // A system the map does not hold (wormhole space) is skipped, and what follows it is then a leap. So is any
        // step to a system that is not a gate neighbour: the jump the trail missed is never drawn in.
        var steps = new List<MapTrailStep>();
        int previous = -1;
        foreach (MapTrailPoint point in points)
        {
            if (!graph.TryGetIndex(point.SolarSystemId, out int index) || index == previous)
                continue;
            steps.Add(new MapTrailStep(index, previous >= 0 && !graph.NeighboursOf(previous).Contains(index)));
            previous = index;
        }
        Trail = steps;
    }
}
