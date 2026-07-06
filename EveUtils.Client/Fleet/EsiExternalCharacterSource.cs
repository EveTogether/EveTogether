using EveUtils.Client.Esi;
using EveUtils.Shared.DependencyInjection;

namespace EveUtils.Client.Fleet;

/// <summary>
/// Public-ESI implementation of <see cref="IExternalCharacterEsiSource"/> (was the body of
/// <see cref="ExternalCharacterLookup"/> before the cache was added). Composes
/// <see cref="ICharacterInfoService"/>'s name + affiliation lookups (metered pipeline): a resolved name
/// marks the character as existing; corp/alliance labels are attached when available. Auto-registered as a
/// singleton (lifetime marker).
/// </summary>
public sealed class EsiExternalCharacterSource(ICharacterInfoService characterInfo) : IExternalCharacterEsiSource, ISingletonService
{
    public async Task<ExternalCharacterInfo> FetchAsync(int characterId, CancellationToken cancellationToken = default)
    {
        if (characterId <= 0)
            return ExternalCharacterInfo.Unknown(characterId);

        // Affiliation is purely cosmetic; the refresh also primes the name cache so the existence check below is
        // a cache hit. A null affiliation just leaves the labels empty.
        var affiliation = await characterInfo.RefreshAsync(characterId, cancellationToken);
        var name = await characterInfo.GetNameAsync(characterId, cancellationToken);
        if (string.IsNullOrEmpty(name))
            return ExternalCharacterInfo.Unknown(characterId); // 404 / unreachable → not found

        return new ExternalCharacterInfo(characterId, name, affiliation?.CorporationName, affiliation?.AllianceName, Exists: true);
    }
}
