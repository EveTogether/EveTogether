namespace EveUtils.Shared.Modules.Esi;

/// <summary>
/// Resolves a character's public identity (name + corp/alliance name/ticker) through the metered ESI pipeline
/// <c>/characters</c> → <c>/corporations</c> + <c>/alliances</c>, all public (no token). The single
/// source both the client UI and the server's pairing flow use, so every public affiliation lookup goes through
/// the same rate-limited, file-cached, metered <see cref="Http.IEsiClient"/> — no bare HttpClient bypass.
/// </summary>
public interface IEsiAffiliationResolver
{
    /// <summary>
    /// Resolves the character through public ESI. Returns null only when the character itself does not resolve
    /// (404 / unreachable); corp and alliance are best-effort, so their fields may be null on a partial result.
    /// </summary>
    Task<EsiCharacterAffiliation?> ResolveAsync(int characterId, CancellationToken cancellationToken = default);
}
