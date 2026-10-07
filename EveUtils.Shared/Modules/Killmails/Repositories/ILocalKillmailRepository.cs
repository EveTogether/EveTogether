using EveUtils.Shared.Modules.Killmails.Dtos;
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

    /// <summary>Makes a fleet mate's rows shared in <paramref name="fleetId"/> on <paramref name="serverIdentity"/> match their latest full share, in one
    /// transaction: adds the missing <paramref name="fetched"/> mails and removes the rows of that fleet whose id is no
    /// longer in <paramref name="sharedKillmailIds"/>. Rows without that fleet id (own imports, other fleets) are never
    /// touched. Returns whether anything changed, and the runs a withdrawn row was linked to.</summary>
    Task<(bool Changed, IReadOnlyList<Guid> WithdrawnFromRunIds)> ReconcileFleetShareAsync(int characterId, string serverIdentity, long fleetId, IReadOnlyCollection<int> sharedKillmailIds,
        IReadOnlyList<LocalKillmail> fetched, CancellationToken cancellationToken = default);

    /// <summary>Fills in the victim position on every row of each killmail that has none yet, whichever character it is
    /// stored under (ET-473). Returns the characters whose rows changed.</summary>
    Task<IReadOnlyList<int>> SetPositionsAsync(IReadOnlyDictionary<int, KillmailPosition> positionsByKillmailId, CancellationToken cancellationToken = default);
}
