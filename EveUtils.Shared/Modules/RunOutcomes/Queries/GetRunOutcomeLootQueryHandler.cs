using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Permissions.Repositories;
using EveUtils.Shared.Modules.RunOutcomes.Dtos;
using EveUtils.Shared.Modules.RunOutcomes.Entities;
using EveUtils.Shared.Modules.RunOutcomes.Enums;
using EveUtils.Shared.Modules.RunOutcomes.Repositories;

namespace EveUtils.Shared.Modules.RunOutcomes.Queries;

internal sealed class GetRunOutcomeLootQueryHandler(IRunOutcomeReader outcomes, IPermissionToggleStore toggles)
    : IQueryHandler<GetRunOutcomeLootQuery, Result<ActivityOutcomeLootDto>>
{
    public async Task<Result<ActivityOutcomeLootDto>> Handle(GetRunOutcomeLootQuery query,
        CancellationToken cancellationToken = default)
    {
        if (RunOutcomesGate.Refusal(toggles) is { } refusal)
            return Result<ActivityOutcomeLootDto>.Failure(refusal);

        IReadOnlyList<(RunOutcome Outcome, double Weight)> weighted = RunOutcomeKeys.Normalised(query.Key) is { } key
            ? RunOutcomeWeights.Of(await outcomes.ListWithLootAsync(key.Code, RunOutcomesGate.Since(query.Since), cancellationToken))
            : [];
        int pilotCount = RunOutcomeWeights.PilotCount(weighted);
        SharedOutcomesAvailability availability = RunOutcomeWeights.Availability(pilotCount);
        return Result<ActivityOutcomeLootDto>.Success(new ActivityOutcomeLootDto
        {
            Availability = availability,
            PilotCount = pilotCount,
            Outcomes = availability is SharedOutcomesAvailability.Shared
                ? [.. weighted.Select(entry => new RunOutcomeLootDto
                {
                    CompletedOn = DateOnly.FromDateTime(entry.Outcome.CompletedOn),
                    FleetSize = entry.Outcome.FleetSize,
                    LootBasis = entry.Outcome.LootBasis,
                    Weight = entry.Weight,
                    Lines = [.. entry.Outcome.LootLines
                        .OrderBy(line => line.Kind)
                        .ThenBy(line => line.TypeId)
                        .Select(line => new RunOutcomeLootLineDto { Kind = line.Kind, TypeId = line.TypeId, Quantity = line.Quantity })]
                })]
                : []
        });
    }
}
