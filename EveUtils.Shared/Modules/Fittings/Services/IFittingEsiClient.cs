using EveUtils.Shared.Modules.Fittings.Dtos;

namespace EveUtils.Shared.Modules.Fittings.Services;

/// <summary>
/// Authenticated ESI calls for the fittings endpoints.
/// The access token is passed per call because it is per-character.
/// </summary>
public interface IFittingEsiClient
{
    /// <summary>
    /// <c>GET /characters/{characterId}/fittings/</c> — requires scope
    /// <c>esi-fittings.read_fittings.v1</c>. Cached per character until ESI's Expires header.
    /// </summary>
    Task<IReadOnlyList<EsiFitting>> GetFittingsAsync(
        int characterId,
        string accessToken,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// <c>POST /characters/{characterId}/fittings/</c> — requires scope
    /// <c>esi-fittings.write_fittings.v1</c>. Returns the new ESI fitting_id.
    /// Invalidates the cached GET result for this character.
    /// </summary>
    Task<int> PostFittingAsync(
        int characterId,
        string accessToken,
        EsiFittingWrite fitting,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// <c>DELETE /characters/{characterId}/fittings/{fittingId}/</c> — requires scope
    /// <c>esi-fittings.write_fittings.v1</c>. Invalidates the cache for this character.
    /// </summary>
    Task DeleteFittingAsync(
        int characterId,
        string accessToken,
        int fittingId,
        CancellationToken cancellationToken = default);
}
