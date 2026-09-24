using EveUtils.Shared.Modules.Killmails.Entities;

namespace EveUtils.Shared.Modules.Killmails.Repositories;

/// <summary>
/// Stores a character's provisional killmails (ET-340) — parsed from pasted clipboard text, shown until the real
/// mail replaces them.
/// </summary>
public interface IProvisionalKillmailRepository
{
    Task AddAsync(ProvisionalKillmail killmail, CancellationToken cancellationToken = default);

    /// <summary>The character's provisional killmails, newest first.</summary>
    Task<IReadOnlyList<ProvisionalKillmail>> GetForCharacterAsync(int characterId, CancellationToken cancellationToken = default);

    /// <summary>Removes every provisional row for the character whose time (to the second), ship and victim name
    /// match a real mail that just landed — the real mail replaces it in full (ET-340).</summary>
    Task RemoveMatchingAsync(int characterId, DateTime killmailTimeUtc, int victimShipTypeId, string victimName,
        CancellationToken cancellationToken = default);
}
