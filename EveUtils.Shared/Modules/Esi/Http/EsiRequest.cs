namespace EveUtils.Shared.Modules.Esi.Http;

/// <summary>
/// A single ESI request as handed to <see cref="IEsiClient.RequestAsync{T}"/> — the one pivot for all ESI
/// traffic. <see cref="CharacterId"/> null = a public call (no pre-flight). For authed calls the
/// pivot checks <see cref="RequiredScopes"/> against the character's granted scopes and ensures a valid
/// token before a byte goes over the wire.
/// </summary>
/// <param name="Path">ESI path relative to the data base URL, e.g. <c>/characters/123/fittings/</c>.</param>
/// <param name="Method">HTTP method (defaults to GET via the factory helpers).</param>
/// <param name="CharacterId">The character the call is made for; null for a public call.</param>
/// <param name="RequiredScopes">Scopes the feature needs (declarative per call).</param>
/// <param name="Body">Optional JSON request body for POST/PUT.</param>
/// <param name="CompatibilityDateOverride">Optional per-call <c>X-Compatibility-Date</c> (Deel 6); null = pinned default.</param>
/// <param name="ExpectedNotFound">When true a 404 is a fully expected, caller-handled outcome (e.g. "character is not
/// in a fleet") and the pivot logs it at Debug instead of Warning, so a routine 404 on a 60s poll doesn't fill the log.</param>
public sealed record EsiRequest(
    string Path,
    HttpMethod Method,
    int? CharacterId = null,
    IReadOnlyList<string>? RequiredScopes = null,
    string? Body = null,
    string? CompatibilityDateOverride = null,
    bool ExpectedNotFound = false)
{
    public IReadOnlyList<string> Scopes => RequiredScopes ?? [];

    public static EsiRequest Get(string path, int? characterId = null, IReadOnlyList<string>? requiredScopes = null,
        bool expectedNotFound = false) =>
        new(path, HttpMethod.Get, characterId, requiredScopes, ExpectedNotFound: expectedNotFound);

    public static EsiRequest Post(string path, string body, int? characterId = null, IReadOnlyList<string>? requiredScopes = null) =>
        new(path, HttpMethod.Post, characterId, requiredScopes, body);

    public static EsiRequest Put(string path, string body, int? characterId = null, IReadOnlyList<string>? requiredScopes = null) =>
        new(path, HttpMethod.Put, characterId, requiredScopes, body);

    public static EsiRequest Delete(string path, int? characterId = null, IReadOnlyList<string>? requiredScopes = null) =>
        new(path, HttpMethod.Delete, characterId, requiredScopes);
}
