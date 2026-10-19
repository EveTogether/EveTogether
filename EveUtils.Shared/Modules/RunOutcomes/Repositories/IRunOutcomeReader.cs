using EveUtils.Shared.Modules.RunOutcomes.Entities;

namespace EveUtils.Shared.Modules.RunOutcomes.Repositories;

/// <summary>The read half of <see cref="IRunOutcomeRepository"/>. Everything outside the run-outcome command handlers
/// takes this one, so an outcome is only ever stored or removed by a handler that signals it.</summary>
public interface IRunOutcomeReader
{
    /// <summary>The outcomes of the activities keyed by <paramref name="keyCodes"/> completed on or after
    /// <paramref name="since"/>, without their loot lines.</summary>
    Task<IReadOnlyList<RunOutcome>> ListAsync(IReadOnlyCollection<string> keyCodes, DateOnly since,
        CancellationToken cancellationToken = default);

    /// <summary>As <see cref="ListAsync"/> for one activity, with the loot lines.</summary>
    Task<IReadOnlyList<RunOutcome>> ListWithLootAsync(string keyCode, DateOnly since,
        CancellationToken cancellationToken = default);

    /// <summary>Every outcome completed on or after <paramref name="since"/>, without loot lines.</summary>
    Task<IReadOnlyList<RunOutcome>> ListSinceAsync(DateOnly since, CancellationToken cancellationToken = default);

    Task<int> CountAsync(CancellationToken cancellationToken = default);
}
