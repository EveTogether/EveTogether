using EveUtils.Shared.Modules.Esi;

namespace EveUtils.Shared.Modules.Map;

/// <summary>
/// ESI scope declaration for putting a destination into the EVE client's autopilot from the map. Unlike the read
/// scopes it lets EVE Together change something in the game client, so it is <c>OptIn</c>: never pre-ticked on a
/// fresh sign-in, and granted per character by the pilot who wants the feature. Registered via
/// <c>AddModuleEsiScopes(AutopilotScopeCatalog.Catalog)</c>.
/// </summary>
public static class AutopilotScopeCatalog
{
    public const string WriteWaypoint = "esi-ui.write_waypoint.v1";

    public static IEsiScopeCatalog Catalog { get; } = new AutopilotEsiScopeCatalogImpl();

    private sealed class AutopilotEsiScopeCatalogImpl : IEsiScopeCatalog
    {
        public IReadOnlyList<EsiScopeRequirement> Requirements { get; } =
        [
            new EsiScopeRequirement(WriteWaypoint, EsiScopeTarget.Client, "Set destination",
                "Set your autopilot destination in the EVE client from the map.",
                OptIn: true),
        ];
    }
}
