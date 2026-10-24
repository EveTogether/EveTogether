using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Esi.Http;
using EveUtils.Shared.Modules.Map;
using EveUtils.Shared.Modules.Map.Enums;

namespace EveUtils.Client.Esi;

/// <summary>
/// <see cref="IEsiAutopilotClient"/> over the shared ESI pivot. The call is a body-less POST: everything is in the
/// query string, and the pivot refuses it before it leaves the machine when the character never granted the scope.
/// Singleton.
/// </summary>
public sealed class EsiAutopilotClient(IEsiClient esi) : IEsiAutopilotClient, ISingletonService
{
    private static readonly IReadOnlyList<string> WriteScopes = [AutopilotScopeCatalog.WriteWaypoint];

    public async Task<EsiResult> SetWaypointAsync(int characterId, int solarSystemId, WaypointMode mode, CancellationToken cancellationToken = default) =>
        await esi.RequestAsync<object?>(
            new EsiRequest(PathFor(solarSystemId, mode), HttpMethod.Post, characterId, WriteScopes), cancellationToken);

    public static string PathFor(int solarSystemId, WaypointMode mode)
    {
        bool clearOthers = mode == WaypointMode.SetDestination;
        return string.Create(CultureInfo.InvariantCulture,
            $"/ui/autopilot/waypoint/?destination_id={solarSystemId}&add_to_beginning=false&clear_other_waypoints={(clearOthers ? "true" : "false")}");
    }
}
