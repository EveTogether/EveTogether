using EveUtils.Shared.Modules.Esi;

namespace EveUtils.Shared.Modules.Location;

/// <summary>
/// ESI scope declaration for reading a character's own solar system. Needed by the abyssal countdown, which has no
/// other way to see a pilot leave the abyss — the gamelog writes nothing there.
///
/// <c>OptIn</c> like the fleet scopes: where you are is not something to hand over by default, so it is not
/// pre-ticked on a fresh sign-in. Registered via <c>AddModuleEsiScopes(LocationScopeCatalog.Catalog)</c>.
/// </summary>
public static class LocationScopeCatalog
{
    public const string ReadLocation = "esi-location.read_location.v1";

    public static IEsiScopeCatalog Catalog { get; } = new LocationEsiScopeCatalogImpl();

    private sealed class LocationEsiScopeCatalogImpl : IEsiScopeCatalog
    {
        public IReadOnlyList<EsiScopeRequirement> Requirements { get; } =
        [
            new EsiScopeRequirement(ReadLocation, EsiScopeTarget.Client, "Location",
                "See which solar system you are in, so the abyssal countdown can tell when you are out again.",
                OptIn: true),
        ];
    }
}
