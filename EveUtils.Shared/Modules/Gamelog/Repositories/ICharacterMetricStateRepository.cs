using EveUtils.Shared.Modules.Gamelog.Entities;

namespace EveUtils.Shared.Modules.Gamelog.Repositories;

/// <summary>Persistence for the survive-restart slice of a character's metrics: bounty + mined.</summary>
public interface ICharacterMetricStateRepository
{
    Task<CharacterMetricState?> GetAsync(string characterName, CancellationToken cancellationToken = default);

    Task UpsertAsync(string characterName, long bountyTotal, int kills, string minedJson, CancellationToken cancellationToken = default);
}
