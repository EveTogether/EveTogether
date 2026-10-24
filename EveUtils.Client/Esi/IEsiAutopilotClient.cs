using System.Threading;
using System.Threading.Tasks;
using EveUtils.Shared.Modules.Esi.Http;
using EveUtils.Shared.Modules.Map.Enums;

namespace EveUtils.Client.Esi;

public interface IEsiAutopilotClient
{
    /// <summary>
    /// Puts <paramref name="solarSystemId"/> into the autopilot of the EVE client <paramref name="characterId"/> is
    /// logged into. ESI answers 204 whether or not the client is running, so success only means ESI accepted it.
    /// </summary>
    Task<EsiResult> SetWaypointAsync(int characterId, int solarSystemId, WaypointMode mode, CancellationToken cancellationToken = default);
}
