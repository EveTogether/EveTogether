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

        var corp = character.Value.CorporationId > 0
            ? await esi.GetAsync<EsiCorporationPublic>(
                $"/corporations/{character.Value.CorporationId}/", cancellationToken: cancellationToken)
            : null;
        var alliance = character.Value.AllianceId is > 0
            ? await esi.GetAsync<EsiAlliancePublic>(
                $"/alliances/{character.Value.AllianceId}/", cancellationToken: cancellationToken)
            : null;

        var corpValue = corp is { IsSuccess: true, Value: not null } ? corp.Value : null;
        var allyValue = alliance is { IsSuccess: true, Value: not null } ? alliance.Value : null;

        return new EsiCharacterAffiliation(
            NullIfEmpty(character.Value.Name),
            corpValue?.Name, NullIfEmpty(corpValue?.Ticker),
            allyValue?.Name, NullIfEmpty(allyValue?.Ticker));
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
