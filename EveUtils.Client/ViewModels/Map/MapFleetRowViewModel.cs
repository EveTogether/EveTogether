using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Client.WorldMap;

namespace EveUtils.Client.ViewModels.Map;

/// <summary>One of your active fleets in the FOLLOW list.</summary>
public sealed partial class MapFleetRowViewModel(MapFleetChoice fleet) : ObservableObject
{
    public MapFleetChoice Fleet { get; } = fleet;

    public string Name => Fleet.Name;

    [ObservableProperty] private string _membersText = "reading the roster…";
    [ObservableProperty] private bool _isFollowed;
}
