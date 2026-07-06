using EveUtils.Client.Dialogs;
using EveUtils.Client.Transport;
using EveUtils.Client.ViewModels;
using EveUtils.Shared.DependencyInjection;

namespace EveUtils.Client.Fleet;

/// <summary>
/// Default <see cref="IFleetMetricsLauncher"/>: fetches the fleet (unless the caller supplies it), wraps it in a
/// <see cref="ServerFleetClient"/> and shows the view-only metrics window. Holds the service provider only to build
/// the runtime-parameterised <see cref="FleetMetricsViewModel"/> — the same construction the fleet window does.
/// </summary>
internal sealed class FleetMetricsLauncher(
    IFleetTransportClient fleets, IDialogService dialogs, IServiceProvider services)
    : IFleetMetricsLauncher, ISingletonService
{
    public async Task LaunchAsync(string serverAddress, long fleetId, int actingCharacterId, FleetInfo? fleet = null,
        CancellationToken cancellationToken = default)
    {
        fleet ??= await fleets.GetFleetAsync(serverAddress, fleetId, actingCharacterId, cancellationToken);
        if (fleet is null)
            return;

        var client = new ServerFleetClient(fleets, serverAddress, actingCharacterId);
        dialogs.ShowFleetMetrics(new FleetMetricsViewModel(services, client, fleet));
    }
}
