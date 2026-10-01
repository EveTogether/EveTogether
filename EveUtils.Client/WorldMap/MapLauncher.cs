using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Fleet;
using EveUtils.Client.Imaging;
using EveUtils.Client.Opsec;
using EveUtils.Client.ViewModels.Map;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.WorldMap;

/// <summary>A fresh view model per open; the map itself is shared, built once per SDE build by the map module, so a second
/// view costs no second read of the SDE.</summary>
public sealed class MapLauncher(IServiceProvider services, IDialogService dialogs) : IMapLauncher, ISingletonService, IDisposable
{
    // Each map runs a badge timer on the thread pool; one that outlives its screen keeps posting to the dispatcher. The
    // owner of a map still disposes it — this only catches what is left when the app (or a test host) shuts down.
    private readonly List<WeakReference<MapViewModel>> _created = [];

    public event Action<MapViewModel>? MapOpened;

    public MapViewModel Create()
    {
        MapViewModel map = _Build();
        lock (_created)
        {
            _created.RemoveAll(reference => !reference.TryGetTarget(out _));
            _created.Add(new WeakReference<MapViewModel>(map));
        }

        return map;
    }

    public void Dispose()
    {
        lock (_created)
        {
            foreach (WeakReference<MapViewModel> reference in _created)
                if (reference.TryGetTarget(out MapViewModel? map))
                    map.Dispose();
            _created.Clear();
        }
    }

    private MapViewModel _Build() => new(
        services.GetRequiredService<IDispatcher>(),
        services.GetRequiredService<ICharacterRegistry>(),
        services.GetRequiredService<IFleetPositionSource>(),
        services.GetRequiredService<MapTrailRecorder>(),
        services.GetRequiredService<IMapFleetSource>(),
        services.GetRequiredService<TimeProvider>(),
        services.GetService<ICharacterPortraitProvider>(),
        dialogs,
        services.GetRequiredService<IOpsecService>());

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
