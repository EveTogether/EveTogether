namespace EveUtils.Client.Fleet;

/// <summary>
/// The raw public-ESI fetch behind <see cref="ExternalCharacterLookup"/> — the seam the cache wraps.
/// Best-effort: an unknown id / unreachable ESI resolves to an <see cref="ExternalCharacterInfo"/> with
/// <c>Exists=false</c> rather than throwing. Splitting this out lets the cache decide <i>when</i> to call ESI
/// (and lets a headless check substitute a counting stub) without touching the lookup orchestration.
/// </summary>
public interface IExternalCharacterEsiSource
{
    Task<ExternalCharacterInfo> FetchAsync(int characterId, CancellationToken cancellationToken = default);
}
