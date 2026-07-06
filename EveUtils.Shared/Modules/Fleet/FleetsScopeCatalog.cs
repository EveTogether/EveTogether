using EveUtils.Shared.Modules.Esi;

namespace EveUtils.Shared.Modules.Fleet;

/// <summary>
/// ESI scope declarations for the in-game fleet coupling. Both scopes are <c>OptIn</c>: the
/// read scope is requested only when the user enables fleet-coupling for a character (Q1 — opt-in per character, not
/// default-on), and the write scope (FC control: invite/move/kick/MOTD/wings) is opted into on top of that. They are
/// declared here so the per-call scope check (<see cref="EsiScopeRequirement"/>) and the consent UI know them;
/// granting them stays the user's explicit choice. Registered via <c>AddModuleEsiScopes(FleetsScopeCatalog.Catalog)</c>.
/// </summary>
public static class FleetsScopeCatalog
{
    public const string ReadFleet = "esi-fleets.read_fleet.v1";
    public const string WriteFleet = "esi-fleets.write_fleet.v1";

    public static IEsiScopeCatalog Catalog { get; } = new FleetsEsiScopeCatalogImpl();

    private sealed class FleetsEsiScopeCatalogImpl : IEsiScopeCatalog
    {
        public IReadOnlyList<EsiScopeRequirement> Requirements { get; } =
        [
            new EsiScopeRequirement(ReadFleet, EsiScopeTarget.Client, "Fleet",
                "See your live in-game fleet (roster, wings/squads) inside EVE Together.", OptIn: true),
            new EsiScopeRequirement(WriteFleet, EsiScopeTarget.Client, "Fleet",
                "Let the FC drive the in-game fleet from EVE Together (invite, move, kick, MOTD, wings/squads).", OptIn: true),
        ];
    }
}
