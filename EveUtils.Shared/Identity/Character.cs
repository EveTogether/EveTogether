namespace EveUtils.Shared.Identity;

/// <summary>
/// First-class identity (foundation pillar 1). v1 = a locally known character keyed by
/// <see cref="EsiCharacterId"/> once ESI auth is wired (v1.x). <see cref="GrantedScopes"/> contains
/// the ESI scopes actually granted by EVE for this character (partial grants accepted).
/// </summary>
public sealed record Character(
    string Name,
    int? EsiCharacterId = null,
    IReadOnlyList<string>? GrantedScopes = null)
{
    /// <summary>Whether this character has the given ESI scope.</summary>
    public bool HasScope(string scope) =>
        GrantedScopes is not null && GrantedScopes.Contains(scope, StringComparer.OrdinalIgnoreCase);
}
