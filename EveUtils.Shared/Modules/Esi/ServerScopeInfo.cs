namespace EveUtils.Shared.Modules.Esi;

/// <summary>
/// Describes an optional ESI scope that the server declares it may use.
/// The client shows these as opt-in checkboxes before the Mode B pairing redirect.
/// </summary>
public sealed record ServerScopeInfo(
    string Scope,
    string Reason,
    string Feature);

/// <summary>Response shape for <c>GET /api/server/scopes</c>. <c>ServerName</c> lets the couple
/// dialog show the server's own name before pairing; null on older servers.</summary>
public sealed record ServerScopesResponse(
    IReadOnlyList<string> RequiredScopes,
    IReadOnlyList<ServerScopeInfo> OptionalScopes,
    string? ServerName = null);
