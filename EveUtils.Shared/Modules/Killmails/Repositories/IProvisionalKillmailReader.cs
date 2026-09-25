using EveUtils.Shared.Modules.Killmails.Entities;

namespace EveUtils.Shared.Modules.Killmails.Repositories;

/// <summary>
/// The read half of <see cref="IProvisionalKillmailRepository"/> (ET-383). Everything outside the killmail command
/// handlers takes this one, so a stored or removed provisional row always arrives through a handler that tells the
/// killmail screens.
/// </summary>
public interface IProvisionalKillmailReader
{
    /// <summary>The character's provisional killmails, newest first.</summary>
    Task<IReadOnlyList<ProvisionalKillmail>> GetForCharacterAsync(int characterId, CancellationToken cancellationToken = default);
}
