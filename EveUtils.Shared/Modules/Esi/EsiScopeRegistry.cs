namespace EveUtils.Shared.Modules.Esi;

/// <summary>
/// In-memory, startup-built scope registry. Collects all <see cref="IEsiScopeCatalog"/>s
/// registered by modules and groups them by target for fast lookup. The base scope
/// <c>publicData</c> is always included for both hosts.
/// </summary>
public sealed class EsiScopeRegistry : IEsiScopeRegistry
{
    private const string PublicDataScope = "publicData";

    private readonly IReadOnlyList<EsiScopeRequirement> _all;

    public EsiScopeRegistry(IEnumerable<IEsiScopeCatalog> catalogs)
    {
        var collected = catalogs
            .SelectMany(c => c.Requirements)
            .ToList();

        // publicData is always required on both hosts and does not need to be declared per module.
        if (!collected.Any(r => r.Scope == PublicDataScope))
            collected.Insert(0, new EsiScopeRequirement(PublicDataScope, EsiScopeTarget.Both, "Identity", "Required for ESI authentication."));

        _all = collected;
    }

    public IReadOnlyList<string> GetScopes(EsiScopeTarget host) =>
        GetRequirements(host)
            .Select(r => r.Scope)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public IReadOnlyList<EsiScopeRequirement> GetRequirements(EsiScopeTarget host) =>
        _all.Where(r => (r.Target & host) != 0).ToList();

    public bool IsRequired(string scope, EsiScopeTarget host) =>
        _all.Any(r => (r.Target & host) != 0 &&
                      string.Equals(r.Scope, scope, StringComparison.OrdinalIgnoreCase));
}
