using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Esi.Http;

namespace EveUtils.Shared.Modules.Esi;

/// <summary>
/// Resolves a character's public identity through the metered ESI pivot (<see cref="IEsiClient"/>): the
/// <c>/characters</c> call yields the name + corp/alliance ids, then <c>/corporations</c> + <c>/alliances</c>
/// fill in names/tickers. Going through the pivot means these public lookups are rate-limited, file-cached at
/// ESI's own TTLs and visible in the ESI metrics — replacing the former bare-HttpClient bypass clients.
/// Best-effort: a failed corp/alliance leg just leaves those fields null.
/// </summary>
public sealed class EsiAffiliationResolver(IEsiClient esi) : IEsiAffiliationResolver, ISingletonService
{
    public async Task<EsiCharacterAffiliation?> ResolveAsync(int characterId, CancellationToken cancellationToken = default)
    {
        if (characterId <= 0)
            return null;

        var character = await esi.GetAsync<EsiCharacterPublic>(
            $"/characters/{characterId}/", cancellationToken: cancellationToken);
        if (character is not { IsSuccess: true, Value: not null })
            return null;

        var corp = await _FetchCorporationAsync(character.Value.CorporationId, cancellationToken);
        var alliance = await _FetchAllianceAsync(character.Value.AllianceId ?? 0, cancellationToken);

        return new EsiCharacterAffiliation(
            NullIfEmpty(character.Value.Name),
            corp?.Name, NullIfEmpty(corp?.Ticker),
            alliance?.Name, NullIfEmpty(alliance?.Ticker));
    }

    public async Task<string?> ResolveCorporationNameAsync(int corporationId, CancellationToken cancellationToken = default) =>
        NullIfEmpty((await _FetchCorporationAsync(corporationId, cancellationToken))?.Name);

    public async Task<string?> ResolveAllianceNameAsync(int allianceId, CancellationToken cancellationToken = default) =>
        NullIfEmpty((await _FetchAllianceAsync(allianceId, cancellationToken))?.Name);

    // Shared by ResolveAsync and the two name-only methods above, so resolving a killmail's corp/alliance id never
    // doubles the ESI call ResolveAsync already makes for a character's own affiliation.
    private async Task<EsiCorporationPublic?> _FetchCorporationAsync(int corporationId, CancellationToken cancellationToken)
    {
        if (corporationId <= 0)
        {
            return null;
        }
        var result = await esi.GetAsync<EsiCorporationPublic>($"/corporations/{corporationId}/", cancellationToken: cancellationToken);
        return result is { IsSuccess: true, Value: not null } ? result.Value : null;
    }

    private async Task<EsiAlliancePublic?> _FetchAllianceAsync(int allianceId, CancellationToken cancellationToken)
    {
        if (allianceId <= 0)
        {
            return null;
        }
        var result = await esi.GetAsync<EsiAlliancePublic>($"/alliances/{allianceId}/", cancellationToken: cancellationToken);
        return result is { IsSuccess: true, Value: not null } ? result.Value : null;
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
