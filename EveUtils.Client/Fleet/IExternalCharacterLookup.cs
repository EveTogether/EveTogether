namespace EveUtils.Client.Fleet;

/// <summary>
/// Verifies an external EVE character via PUBLIC ESI before the owner adds it to a fleet on trust.
/// Best-effort: any failure (unknown id, public ESI unreachable) resolves to <see cref="ExternalCharacterInfo"/>
/// with <c>Exists=false</c> rather than throwing. Reuses the cached public-ESI client (no own HTTP).
/// </summary>
public interface IExternalCharacterLookup
{
    Task<ExternalCharacterInfo> LookupAsync(int characterId, CancellationToken cancellationToken = default);
}
