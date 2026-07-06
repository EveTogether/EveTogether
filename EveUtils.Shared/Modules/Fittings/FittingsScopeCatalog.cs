using EveUtils.Shared.Modules.Esi;

namespace EveUtils.Shared.Modules.Fittings;

/// <summary>
/// ESI scope declarations for the Fittings module.
/// Registered via <c>services.AddModuleEsiScopes(FittingsScopes.Catalog)</c> in
/// <c>AddFittingsModule()</c>; the <see cref="IEsiScopeRegistry"/> collects them at startup.
/// </summary>
public static class FittingsScopeCatalog
{
    public const string ReadFittings  = "esi-fittings.read_fittings.v1";
    public const string WriteFittings = "esi-fittings.write_fittings.v1";

    public static IEsiScopeCatalog Catalog { get; } = new FittingsEsiScopeCatalogImpl();

    private sealed class FittingsEsiScopeCatalogImpl : IEsiScopeCatalog
    {
        public IReadOnlyList<EsiScopeRequirement> Requirements { get; } =
        [
            new EsiScopeRequirement(ReadFittings,  EsiScopeTarget.Client, "Fittings",
                "Required to import your saved fits from EVE Online."),
            new EsiScopeRequirement(WriteFittings, EsiScopeTarget.Client, "Fittings",
                "Required to push fits back to EVE Online."),
        ];
    }
}
