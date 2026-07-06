namespace EveUtils.Shared.Modules.Esi;

/// <summary>
/// The verified EVE identity extracted from a validated SSO access-token JWT.
/// <see cref="GrantedScopes"/> contains the <c>scp</c> claim — what EVE actually granted,
/// which may be a subset of what was requested (partial grant).
/// </summary>
public sealed record EsiIdentity(int CharacterId, string CharacterName, IReadOnlyList<string> GrantedScopes);
