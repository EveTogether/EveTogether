using EveUtils.Shared.Modules.Killmails.Entities;

namespace EveUtils.Shared.Modules.Killmails.Repositories;

/// <summary>
/// Stores a character's imported killmails. Adds only, never replaces: a mail that ages out of ESI's 90-day list
/// stays, and a stored mail keeps its run link.
/// </summary>
public interface ILocalKillmailRepository
{
    /// <summary>The subset of <paramref name="killmailIds"/> already stored for the character.</summary>
    Task<IReadOnlySet<int>> GetKnownIdsAsync(int characterId, IReadOnlyCollection<int> killmailIds, CancellationToken cancellationToken = default);

    /// <summary>Stores the killmails with their items and attackers in one transaction, skipping any already stored.</summary>
    Task AddMissingAsync(int characterId, IReadOnlyList<LocalKillmail> killmails, CancellationToken cancellationToken = default);

    /// <summary>The character's killmails with items and attackers, newest first.</summary>
    Task<IReadOnlyList<LocalKillmail>> GetForCharacterAsync(int characterId, CancellationToken cancellationToken = default);
}
