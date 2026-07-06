using EveUtils.Shared.Modules.Esi;

namespace EveUtils.Shared.Modules.Implants;

/// <summary>
/// ESI scope declarations for the Implants module. The implants endpoint
/// (<c>GET /characters/{id}/implants/</c>) returns the active implant type ids and is gated by the clones scope.
/// Registered via <c>services.AddModuleEsiScopes(ImplantsScopeCatalog.Catalog)</c>.
/// </summary>
public static class ImplantsScopeCatalog
{
    public const string ReadImplants = "esi-clones.read_implants.v1";

    public static IEsiScopeCatalog Catalog { get; } = new ImplantsEsiScopeCatalogImpl();

    private sealed class ImplantsEsiScopeCatalogImpl : IEsiScopeCatalog
    {
        public IReadOnlyList<EsiScopeRequirement> Requirements { get; } =
        [
            new EsiScopeRequirement(ReadImplants, EsiScopeTarget.Client, "Implants",
                "Required to use your character's actual implants in fit stats and to show its training-attribute implants."),
        ];
    }
}
