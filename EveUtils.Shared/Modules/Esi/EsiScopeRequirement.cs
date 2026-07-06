namespace EveUtils.Shared.Modules.Esi;

/// <summary>
/// Declares a single ESI scope that a feature requires. Modules register these via
/// <see cref="IEsiScopeCatalog"/> at startup; the <see cref="IEsiScopeRegistry"/> collects them all.
/// </summary>
/// <param name="Scope">The ESI scope string, e.g. <c>esi-fittings.read_fittings.v1</c>.</param>
/// <param name="Target">Which host(s) need this scope.</param>
/// <param name="Feature">Human-readable feature name shown in the scope-consent UI.</param>
/// <param name="Description">Optional explanation shown alongside the consent checkbox.</param>
/// <param name="OptIn">When true the scope is NOT pre-selected on a fresh sign-in (the user must tick it to grant it),
/// for scopes a user opts into rather than gets by default — e.g. ESI fleet access (Q1, 2026-06-14). A re-auth still
/// pre-ticks whatever was already granted, regardless of this flag.</param>
public sealed record EsiScopeRequirement(
    string Scope,
    EsiScopeTarget Target,
    string Feature,
    string Description = "",
    bool OptIn = false);
