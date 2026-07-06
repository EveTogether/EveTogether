using System;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Repositories;

namespace EveUtils.Client.Fleet;

/// <summary>
/// Public-ESI verification for the external-member flow with a persistent 1-day cache.
/// The lookup consults the client-local <see cref="IExternalCharacterCache"/> first: a row younger than
/// <see cref="CacheTtl"/> (24h, measured from <see cref="CachedExternalCharacter.FetchedAtUnixMs"/> against the
/// injected <see cref="TimeProvider"/>) is served without touching ESI. Otherwise it fetches via
/// <see cref="IExternalCharacterEsiSource"/> and upserts the result. Best-effort is preserved: an ESI miss falls
/// back to the stale cached row when there is one, else to <see cref="ExternalCharacterInfo.Unknown"/>.
/// Auto-registered as a singleton (lifetime marker).
/// </summary>
public sealed class ExternalCharacterLookup(
    IExternalCharacterEsiSource esi,
    IExternalCharacterCache cache,
    TimeProvider clock) : IExternalCharacterLookup, ISingletonService
{
    /// <summary>How long a cached row stays fresh before a re-fetch (prompt: re-query public ESI only after a day).</summary>
    public static readonly TimeSpan CacheTtl = TimeSpan.FromDays(1);

    public async Task<ExternalCharacterInfo> LookupAsync(int characterId, CancellationToken cancellationToken = default)
    {
        if (characterId <= 0)
            return ExternalCharacterInfo.Unknown(characterId);

        var nowMs = clock.GetUtcNow().ToUnixTimeMilliseconds();
        var cached = await cache.GetAsync(characterId, cancellationToken);
        if (cached is not null && nowMs - cached.FetchedAtUnixMs < (long)CacheTtl.TotalMilliseconds)
            return ToInfo(cached); // fresh (< 1 day) → no ESI round-trip.

        var fetched = await esi.FetchAsync(characterId, cancellationToken);
        if (!fetched.Exists)
            return cached is not null ? ToInfo(cached) : fetched; // ESI miss → fall back to the (stale) cache if any.

        await cache.UpsertAsync(new CachedExternalCharacter
        {
            CharacterId = characterId,
            Name = fetched.Name,
            Corp = fetched.Corp,
            Alliance = fetched.Alliance,
            FetchedAtUnixMs = nowMs
        }, cancellationToken);

        return fetched;
    }

    private static ExternalCharacterInfo ToInfo(CachedExternalCharacter c) =>
        new(c.CharacterId, c.Name, c.Corp, c.Alliance, Exists: true);
}
