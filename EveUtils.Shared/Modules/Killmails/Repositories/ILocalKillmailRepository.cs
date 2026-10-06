using EveUtils.Shared.Modules.Killmails.Entities;

namespace EveUtils.Shared.Modules.Killmails.Repositories;

/// <summary>
/// Stores a character's imported killmails. Adds only, never replaces: a mail that ages out of ESI's 90-day list
/// stays, and a stored mail keeps its run link. Taken only by the killmail command handlers (ET-383).
/// </summary>
public interface ILocalKillmailRepository : ILocalKillmailReader
{
    /// <summary>Stores the killmails with their items and attackers in one transaction, skipping any already stored; returns the ones it added.</summary>
    Task<IReadOnlyList<LocalKillmail>> AddMissingAsync(int characterId, IReadOnlyList<LocalKillmail> killmails, CancellationToken cancellationToken = default);

    /// <summary>Makes a fleet mate's rows shared in <paramref name="fleetId"/> match their latest full share, in one
    /// transaction: adds the missing <paramref name="fetched"/> mails and removes the rows of that fleet whose id is no
    /// longer in <paramref name="sharedKillmailIds"/>. Rows without that fleet id (own imports, other fleets) are never
    /// touched. Returns whether anything changed.</summary>
    Task<bool> ReconcileFleetShareAsync(int characterId, long fleetId, IReadOnlyCollection<int> sharedKillmailIds,
        IReadOnlyList<LocalKillmail> fetched, CancellationToken cancellationToken = default);
}
