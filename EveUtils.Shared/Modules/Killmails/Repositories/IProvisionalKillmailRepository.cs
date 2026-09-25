using EveUtils.Shared.Modules.Killmails.Entities;

namespace EveUtils.Shared.Modules.Killmails.Repositories;

/// <summary>
/// Stores a character's provisional killmails (ET-340) — parsed from pasted clipboard text, shown until the real
/// mail replaces them. Taken only by the killmail command handlers (ET-383).
/// </summary>
public interface IProvisionalKillmailRepository : IProvisionalKillmailReader
{
    Task AddAsync(ProvisionalKillmail killmail, CancellationToken cancellationToken = default);

    /// <summary>Removes every provisional row for the character whose time (to the second), ship and victim name
    /// match a real mail that just landed — the real mail replaces it in full (ET-340). Returns whether anything was
    /// removed, so the caller publishes no signal for a no-op match.</summary>
    Task<bool> RemoveMatchingAsync(int characterId, DateTime killmailTimeUtc, int victimShipTypeId, string victimName,
        CancellationToken cancellationToken = default);
}
