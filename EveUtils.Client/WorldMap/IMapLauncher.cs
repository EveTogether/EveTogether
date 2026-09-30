using System;
using System.Threading.Tasks;
using EveUtils.Client.ViewModels.Map;

namespace EveUtils.Client.WorldMap;

/// <summary>Builds map view models and opens the MAP tab — from the rail, from a fleet's row and from the fleet card
/// (ET-395), which none of them can do on their own: each would have to know how a map is wired.</summary>
public interface IMapLauncher
{
    /// <summary>Raised for a map the launcher built and put on screen, not for one that was already open. The main window
    /// listens to show what the map follows in its status bar.</summary>
    event Action<MapViewModel>? MapOpened;

    /// <summary>A map that is not on screen: the fleet card's, sharing the graph the MAP tab reads.</summary>
    MapViewModel Create();

    /// <summary>Opens the MAP tab, or brings the open one forward.</summary>
    MapViewModel Open();

    /// <summary>Opens the MAP tab and has it follow this fleet. <paramref name="serverAddress"/> null matches by fleet id
    /// alone. The tab keeps whatever follow the pilot picked when the fleet is not one of theirs.</summary>
    Task OpenFollowingFleetAsync(long fleetId, string? serverAddress);

    /// <summary>The fleet card's POP OUT (ET-396): the same map as <see cref="OpenFollowingFleetAsync"/>, in a window of its
    /// own. A map that is out already follows the fleet and comes forward; a floating map is a window anyway.</summary>
    Task PopOutFollowingFleetAsync(long fleetId, string? serverAddress);
}
