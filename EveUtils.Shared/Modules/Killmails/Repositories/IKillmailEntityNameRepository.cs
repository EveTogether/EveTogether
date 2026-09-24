using EveUtils.Shared.Modules.Killmails.Entities;

namespace EveUtils.Shared.Modules.Killmails.Repositories;

/// <summary>Client-local persistent cache of public names for character, corporation and alliance ids seen on
/// killmails, keyed by id. Backs <c>KillmailNames</c>: a row still within the configured refresh interval is
/// served without an ESI round-trip.</summary>
public interface IKillmailEntityNameRepository
{
    /// <summary>The stored rows among <paramref name="ids"/>, keyed by id. An id with no row is simply absent.</summary>
    Task<IReadOnlyDictionary<long, KillmailEntityName>> GetManyAsync(IReadOnlyCollection<long> ids, CancellationToken cancellationToken = default);

    /// <summary>Insert or update (keyed on id) the resolved name with its refresh timestamp.</summary>
    Task UpsertAsync(KillmailEntityName entry, CancellationToken cancellationToken = default);
}
