using System;
using System.Threading.Tasks;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Fleet;
using EveUtils.Client.Imaging;
using EveUtils.Client.ViewModels.Map;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.WorldMap;

/// <summary>A fresh view model per open; the map itself is shared, built once per SDE build by the map module, so a second
/// view costs no second read of the SDE.</summary>
public sealed class MapLauncher(IServiceProvider services, IDialogService dialogs) : IMapLauncher, ISingletonService
{
    public event Action<MapViewModel>? MapOpened;

    public MapViewModel Create() => new(
        services.GetRequiredService<IDispatcher>(),
        services.GetRequiredService<ICharacterRegistry>(),
        services.GetRequiredService<IFleetPositionSource>(),
        services.GetRequiredService<MapTrailRecorder>(),
        services.GetRequiredService<IMapFleetSource>(),
        services.GetRequiredService<TimeProvider>(),
        services.GetService<ICharacterPortraitProvider>(),
        dialogs);

    public MapViewModel Open()
    {
        MapViewModel map = Create();
        MapViewModel shown = dialogs.ShowMap(map);
        if (ReferenceEquals(shown, map))
            MapOpened?.Invoke(map);
        return shown;
    }

    public async Task OpenFollowingFleetAsync(long fleetId, string? serverAddress)
    {
        MapViewModel map = Open();
        await map.FollowFleetByIdAsync(fleetId, serverAddress);
    }

    public async Task PopOutFollowingFleetAsync(long fleetId, string? serverAddress)
    {
        await OpenFollowingFleetAsync(fleetId, serverAddress);
        dialogs.PopOutMap();
    }
}
