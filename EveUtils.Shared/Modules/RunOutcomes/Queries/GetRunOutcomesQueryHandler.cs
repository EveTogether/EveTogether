using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Permissions.Repositories;
using EveUtils.Shared.Modules.RunOutcomes.Dtos;
using EveUtils.Shared.Modules.RunOutcomes.Entities;
using EveUtils.Shared.Modules.RunOutcomes.Enums;
using EveUtils.Shared.Modules.RunOutcomes.Repositories;
using EveUtils.Shared.Modules.Runs.Earnings;

namespace EveUtils.Shared.Modules.RunOutcomes.Queries;

internal sealed class GetRunOutcomesQueryHandler(IRunOutcomeReader outcomes, IPermissionToggleStore toggles)
    : IQueryHandler<GetRunOutcomesQuery, Result<IReadOnlyList<ActivityOutcomesDto>>>
{
    public async Task<Result<IReadOnlyList<ActivityOutcomesDto>>> Handle(GetRunOutcomesQuery query,
        CancellationToken cancellationToken = default)
    {
        if (RunOutcomesGate.Refusal(toggles) is { } refusal)
            return Result<IReadOnlyList<ActivityOutcomesDto>>.Failure(refusal);

        Dictionary<string, ActivityStatsKey> keys = query.Keys
            .Select(RunOutcomeKeys.Normalised)
            .OfType<ActivityStatsKey>()
            .DistinctBy(key => key.Code)
            .ToDictionary(key => key.Code, StringComparer.Ordinal);
        if (keys.Count == 0)
            return Result<IReadOnlyList<ActivityOutcomesDto>>.Success([]);

        ILookup<string, RunOutcome> stored = (await outcomes.ListAsync(keys.Keys, RunOutcomesGate.Since(query.Since), cancellationToken))
            .ToLookup(outcome => outcome.KeyCode, StringComparer.Ordinal);
        // Every key asked for answers, so a client can tell "too few pilots" apart from a key it got wrong.
        return Result<IReadOnlyList<ActivityOutcomesDto>>.Success(
            [.. keys.Select(key => _Activity(key.Value, [.. stored[key.Key]]))]);
    }

    private static ActivityOutcomesDto _Activity(ActivityStatsKey key, IReadOnlyList<RunOutcome> outcomes)
    {
        IReadOnlyList<(RunOutcome Outcome, double Weight)> weighted = RunOutcomeWeights.Of(outcomes);
        int pilotCount = RunOutcomeWeights.PilotCount(weighted);
        SharedOutcomesAvailability availability = RunOutcomeWeights.Availability(pilotCount);
        return new ActivityOutcomesDto
        {
            Key = key,
            Availability = availability,
            PilotCount = pilotCount,
            Outcomes = availability is SharedOutcomesAvailability.Shared
                ? [.. weighted.Select(entry => new RunOutcomeDto
                {
                    CompletedOn = DateOnly.FromDateTime(entry.Outcome.CompletedOn),
                    DurationSeconds = entry.Outcome.DurationSeconds,
                    FixedIsk = entry.Outcome.FixedIsk,
                    FleetSize = entry.Outcome.FleetSize,
                    LootBasis = entry.Outcome.LootBasis,
                    OpenUnrecognisedLootCount = entry.Outcome.OpenUnrecognisedLootCount,
                    Weight = entry.Weight
                })]
                : []
        };
    }
}
