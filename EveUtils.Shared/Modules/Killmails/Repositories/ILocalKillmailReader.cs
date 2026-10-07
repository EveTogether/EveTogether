using EveUtils.Shared.Modules.Killmails.Entities;

namespace EveUtils.Shared.Modules.Killmails.Repositories;

/// <summary>
/// The read half of <see cref="ILocalKillmailRepository"/> (ET-383). Everything outside the killmail command handlers
/// takes this one, so a stored killmail always arrives through a handler that tells the killmail screens.
/// </summary>
public interface ILocalKillmailReader
{
    /// <summary>The subset of <paramref name="killmailIds"/> already stored for the character.</summary>
    Task<IReadOnlySet<int>> GetKnownIdsAsync(int characterId, IReadOnlyCollection<int> killmailIds, CancellationToken cancellationToken = default);

    /// <summary>The character's killmails with items and attackers, newest first.</summary>
    Task<IReadOnlyList<LocalKillmail>> GetForCharacterAsync(int characterId, CancellationToken cancellationToken = default);

    /// <summary>Every stored mail, of any character, that has no victim position yet (ET-473): one entry per killmail id.</summary>
    Task<IReadOnlyList<(int KillmailId, string Hash)>> GetWithoutPositionAsync(CancellationToken cancellationToken = default);

    /// <summary>Up to <paramref name="limit"/> stored mails, of any character, with a player attacker that has no security
    /// status yet (ET-477): one entry per killmail id. NPC attackers are not asked for, so a mail ESI gives no value for
    /// is not read again forever.</summary>
    Task<IReadOnlyList<(int KillmailId, string Hash)>> GetWithoutAttackerSecurityStatusAsync(int limit, CancellationToken cancellationToken = default);
}
