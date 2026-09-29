using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Controls.Map;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Gamelog;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Map.Dtos;
using EveUtils.Shared.Modules.Map.Enums;
using EveUtils.Shared.Modules.Map.Queries;

namespace EveUtils.Client.ViewModels.Map;

/// <summary>
/// The MAP module (ET-392): New Eden to zoom around in, a wayfinder over the local map, a system search and your own
/// characters where the game log last saw them. The graph comes from <see cref="GetMapGraphQuery"/>, which builds it
/// off the UI thread once per SDE build (ET-298); everything here only reads that immutable snapshot.
/// Following a character and the trail come in the next phase of ET-390.
/// </summary>
public sealed partial class MapViewModel : ObservableObject, IRefreshableModule, IDisposable
{
    /// <summary>Width of the route's security bar: the side panel's inner width.</summary>
    public const double SecurityBarWidth = 258;

    private readonly IDispatcher _dispatcher;
    private readonly ICharacterRegistry _registry;
    private readonly GamelogClientService? _gamelog;
    private IReadOnlyList<Character> _characters = [];

    public MapViewModel(IDispatcher dispatcher, ICharacterRegistry registry, GamelogClientService? gamelog)
    {
        _dispatcher = dispatcher;
        _registry = registry;
        _gamelog = gamelog;
        _registry.RegistryChanged += _OnRegistryChanged;
        if (_gamelog is not null)
            _gamelog.LocationChanged += _OnLocationChanged;
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
        Result<MapGraphDto> result = await _dispatcher.Query(new GetMapGraphQuery());
        if (!result.IsSuccess || result.Value is not { } graph)
        {
            StatusMessage = result.Messages.FirstOrDefault()?.Text ?? "The map could not be read.";
            _RefreshMarkers();
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
        _RefreshMarkers();
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

    public void Dispose()
    {
        _registry.RegistryChanged -= _OnRegistryChanged;
        if (_gamelog is not null)
            _gamelog.LocationChanged -= _OnLocationChanged;
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
        SelectedIndex = systemIndex;
        FocusRequest = new MapFocusRequest([systemIndex]);
    }

    private double _BarWidth(int? jumps) => jumps is { } count ? SecurityBarWidth * count / Math.Max(1, Route?.Jumps ?? 0) : 0;

    private void _OnRegistryChanged() => Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = LoadAsync());

    private void _OnLocationChanged(string characterName) => Avalonia.Threading.Dispatcher.UIThread.Post(_RefreshMarkers);

    // Own characters where the game log last put them. The log gives a system name, so it is matched against the map
    // by name — exact only, a prefix match would put a character in the wrong system.
    private void _RefreshMarkers()
    {
        if (Graph is not { } graph || _gamelog is null)
        {
            Markers = [];
            return;
        }

        var markers = new List<MapMarker>();
        foreach (Character character in _characters)
        {
            string? location = _gamelog.Snapshot(character.Name).Location;
            if (graph.FindByName(location) is { } system && string.Equals(system.Name, location?.Trim(), StringComparison.OrdinalIgnoreCase))
                markers.Add(new MapMarker(system.Index, character.Name));
        }
        Markers = markers;
        OnPropertyChanged(nameof(SelectedHereText));
        OnPropertyChanged(nameof(HasSelectedHere));
    }
}
