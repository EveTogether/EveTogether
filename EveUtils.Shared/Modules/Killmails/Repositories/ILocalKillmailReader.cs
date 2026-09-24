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
}
