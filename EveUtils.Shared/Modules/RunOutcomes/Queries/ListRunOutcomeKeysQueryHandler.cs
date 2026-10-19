using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Permissions.Repositories;
using EveUtils.Shared.Modules.RunOutcomes.Dtos;
using EveUtils.Shared.Modules.RunOutcomes.Entities;
using EveUtils.Shared.Modules.RunOutcomes.Repositories;

namespace EveUtils.Shared.Modules.RunOutcomes.Queries;

internal sealed class ListRunOutcomeKeysQueryHandler(IRunOutcomeReader outcomes, IPermissionToggleStore toggles)
    : IQueryHandler<ListRunOutcomeKeysQuery, Result<IReadOnlyList<RunOutcomeKeyCountDto>>>
{
    public async Task<Result<IReadOnlyList<RunOutcomeKeyCountDto>>> Handle(ListRunOutcomeKeysQuery query,
        CancellationToken cancellationToken = default)
    {
        if (RunOutcomesGate.Refusal(toggles) is { } refusal)
            return Result<IReadOnlyList<RunOutcomeKeyCountDto>>.Failure(refusal);

        IReadOnlyList<RunOutcome> stored = await outcomes.ListSinceAsync(RunOutcomesGate.Since(query.Since), cancellationToken);
        return Result<IReadOnlyList<RunOutcomeKeyCountDto>>.Success(
        [
            .. stored
                .GroupBy(outcome => outcome.KeyCode, StringComparer.Ordinal)
                .Select(activity =>
                {
                    IReadOnlyList<(RunOutcome Outcome, double Weight)> weighted = RunOutcomeWeights.Of([.. activity]);
                    return new RunOutcomeKeyCountDto
                    {
                        Key = RunOutcomeKeys.Of(activity.First()),
                        RunCount = weighted.Count(entry => entry.Weight > 0),
                        EffectiveRunCount = weighted.Sum(entry => entry.Weight),
                        PilotCount = RunOutcomeWeights.PilotCount(weighted)
                    };
                })
                .OrderByDescending(activity => activity.EffectiveRunCount)
                .ThenBy(activity => activity.Key.Code, StringComparer.Ordinal)
        ]);
    }
}
