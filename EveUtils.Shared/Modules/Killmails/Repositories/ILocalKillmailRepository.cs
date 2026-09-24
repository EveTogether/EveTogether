using EveUtils.Shared.Modules.Killmails.Entities;

namespace EveUtils.Shared.Modules.Killmails.Repositories;

/// <summary>
/// Stores a character's imported killmails. Adds only, never replaces: a mail that ages out of ESI's 90-day list
/// stays, and a stored mail keeps its run link. Taken only by the killmail command handlers (ET-383).
/// </summary>
public interface ILocalKillmailRepository : ILocalKillmailReader
{
    /// <summary>Stores the killmails with their items and attackers in one transaction, skipping any already stored.</summary>
    Task AddMissingAsync(int characterId, IReadOnlyList<LocalKillmail> killmails, CancellationToken cancellationToken = default);
}
