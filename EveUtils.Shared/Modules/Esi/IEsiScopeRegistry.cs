namespace EveUtils.Shared.Modules.Esi;

/// <summary>
/// Startup-built, in-memory registry of all ESI scope requirements declared by modules.
/// Built once from registered <see cref="IEsiScopeCatalog"/>s; never stored.
/// </summary>
public interface IEsiScopeRegistry
{
    /// <summary>
    /// All scope strings applicable for the given target. Pass <see cref="EsiScopeTarget.Client"/> on
    /// the client, <see cref="EsiScopeTarget.Server"/> on the server; <see cref="EsiScopeTarget.Both"/>
    /// is always included in both.
    /// </summary>
    IReadOnlyList<string> GetScopes(EsiScopeTarget host);

    /// <summary>All requirements with their metadata (feature name, description, target).</summary>
    IReadOnlyList<EsiScopeRequirement> GetRequirements(EsiScopeTarget host);

    /// <summary>Whether any registered requirement declares the given scope for the target host.</summary>
    bool IsRequired(string scope, EsiScopeTarget host);
}
