using System;
using System.Threading;
using System.Threading.Tasks;

namespace EveUtils.Client.Esi;

/// <summary>
/// Resolves and caches a character's public affiliation (corp + alliance name/ticker) through the metered ESI
/// pipeline — the single source of truth for the UI's "Corp [TICK] · Alliance [TICK]" label. The
/// pipeline's file cache honours ESI's own TTLs (character ~1 day, corporation ~1 hour), so a refresh inside the
/// window is a cheap cache hit. <see cref="AffiliationChanged"/> fires when a refresh changes a character's
/// resolved info, so the UI can update live while the app stays open.
/// </summary>
public interface ICharacterInfoService
{
    /// <summary>The last resolved affiliation for the character, or null when it has not been resolved yet.</summary>
    CharacterPublicInfo? GetCached(int characterId);

    /// <summary>
    /// The character's own public name (no token), or null when the id does not resolve (404 / unreachable).
    /// Cached per id; sourced from the same metered <c>/characters/{id}/</c> call that backs the affiliation, so
    /// a prior <see cref="RefreshAsync"/> makes this a cache hit. Used by the external-member flow to verify a
    /// character exists before the owner adds it.
    /// </summary>
    Task<string?> GetNameAsync(int characterId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the character's affiliation through the metered ESI pipeline, updates the cache and raises
    /// <see cref="AffiliationChanged"/> on a change. Best-effort: a transient failure keeps the last good value.
    /// </summary>
    Task<CharacterPublicInfo?> RefreshAsync(int characterId, CancellationToken cancellationToken = default);

    /// <summary>Raised off the UI thread when a refresh changes a character's affiliation; the int is the character id.</summary>
    event Action<int, CharacterPublicInfo?> AffiliationChanged;
}
