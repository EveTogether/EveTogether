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
using EveUtils.Shared.Modules.Map.Dtos;
using EveUtils.Shared.Modules.Map.Enums;
using EveUtils.Shared.Modules.Map.Queries;

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
/// </summary>
public sealed partial class MapViewModel : ObservableObject, IRefreshableModule, IDisposable
{
    /// <summary>Width of the route's security bar: the side panel's inner width.</summary>
    public const double SecurityBarWidth = 258;

    /// <summary>Following never zooms out past system view: the level where a jump is something you can see.</summary>
    public const double FollowMinZoom = 9;

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
    private readonly TimeProvider _clock;
    private readonly ICharacterPortraitProvider? _portraits;
    private IReadOnlyList<Character> _characters = [];

    public MapViewModel(IDispatcher dispatcher, ICharacterRegistry registry, IFleetPositionSource positions, MapTrailRecorder trails,
        TimeProvider clock, ICharacterPortraitProvider? portraits = null)
    {
        _dispatcher = dispatcher;
        _registry = registry;
        _positions = positions;
        _trails = trails;
        _clock = clock;
        _portraits = portraits;
        _registry.RegistryChanged += _OnRegistryChanged;
        _positions.PositionChanged += _OnPositionChanged;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountsText))]
    private MapGraphDto? _graph;

    /// <summary>Why there is no map, or null once it shows.</summary>
    [ObservableProperty] private string? _statusMessage = "Loading the map…";

    [ObservableProperty] private IReadOnlyList<string> _systemNames = [];
    [ObservableProperty] private IReadOnlyList<MapMarker> _markers = [];
    [ObservableProperty] private MapFocusRequest? _focusRequest;
    [ObservableProperty] private bool _isLegendOpen;

    // ── Follow ───────────────────────────────────────────────────────────────────────────────────

    public ObservableCollection<MapCharacterRowViewModel> Characters { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFollowOff), nameof(IsFollowCharacter), nameof(NeedsCharacterPick))]
    private MapFollowMode _followMode = MapFollowMode.Off;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFollowing), nameof(NeedsCharacterPick), nameof(FollowChipText), nameof(FollowStatusText), nameof(TrailResetText))]
    private MapCharacterRowViewModel? _followedCharacter;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FollowChipText), nameof(FollowStatusText))]
    private bool _isFollowPaused;

    /// <summary>The system of the followed character, ringed on the map; -1 when there is none or the map does not hold it.</summary>
    [ObservableProperty] private int _followIndex = -1;

    public bool IsFollowOff => FollowMode == MapFollowMode.Off;
    public bool IsFollowCharacter => FollowMode == MapFollowMode.Character;
    public bool IsFollowing => FollowMode == MapFollowMode.Character && FollowedCharacter is not null;
    public bool NeedsCharacterPick => FollowMode == MapFollowMode.Character && FollowedCharacter is null;
    public bool HasCharacters => Characters.Count > 0;

    public string FollowChipText => FollowedCharacter is not { } followed
        ? string.Empty
        : IsFollowPaused ? "PAUSED · you moved the map" : "FOLLOWING " + followed.Name.ToUpperInvariant();

    /// <summary>The status bar's line; empty when the map follows nobody.</summary>
    public string FollowStatusText => IsFollowing && FollowedCharacter is { } followed
        ? IsFollowPaused ? $"Map: following {followed.Name} (paused)" : $"Map: following {followed.Name}"
        : string.Empty;

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
        _RefreshPositions();
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
            _StopFollowing();
    }

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
        _registry.RegistryChanged -= _OnRegistryChanged;
        _positions.PositionChanged -= _OnPositionChanged;
        _StopFollowing();
    }

    private void _StopFollowing()
    {
        FollowedCharacter = null;
        IsFollowPaused = false;
        foreach (MapCharacterRowViewModel row in Characters)
            row.IsFollowed = false;
        FollowIndex = -1;
        Trail = null;
    }

    private void _FocusFollowed()
    {
        if (FollowIndex >= 0)
            FocusRequest = new MapFocusRequest([FollowIndex], FollowMinZoom);
    }

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
        if (IsFollowing && !IsFollowPaused && FollowedCharacter?.CharacterId == position.CharacterId)
            _FocusFollowed();
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
