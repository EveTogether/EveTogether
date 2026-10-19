using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Permissions.Repositories;
using EveUtils.Shared.Modules.RunOutcomes.Dtos;
using EveUtils.Shared.Modules.RunOutcomes.Repositories;

namespace EveUtils.Shared.Modules.RunOutcomes.Queries;

internal sealed class GetRunOutcomeLootQueryHandler(IRunOutcomeReader outcomes, IPermissionToggleStore toggles)
    : IQueryHandler<GetRunOutcomeLootQuery, Result<IReadOnlyList<RunOutcomeLootDto>>>
{
    public async Task<Result<IReadOnlyList<RunOutcomeLootDto>>> Handle(GetRunOutcomeLootQuery query,
        CancellationToken cancellationToken = default)
    {
        if (RunOutcomesGate.Refusal(toggles) is { } refusal)
            return Result<IReadOnlyList<RunOutcomeLootDto>>.Failure(refusal);

        if (RunOutcomeKeys.Normalised(query.Key) is not { } key)
            return Result<IReadOnlyList<RunOutcomeLootDto>>.Success([]);

        return Result<IReadOnlyList<RunOutcomeLootDto>>.Success(
        [
            .. RunOutcomeWeights.Of(await outcomes.ListWithLootAsync(key.Code, RunOutcomesGate.Since(query.Since), cancellationToken))
                .Select(entry => new RunOutcomeLootDto
                {
                    CompletedOn = DateOnly.FromDateTime(entry.Outcome.CompletedOn),
                    FleetSize = entry.Outcome.FleetSize,
                    LootBasis = entry.Outcome.LootBasis,
                    Weight = entry.Weight,
                    Lines = [.. entry.Outcome.LootLines
                        .OrderBy(line => line.Kind)
                        .ThenBy(line => line.TypeId)
                        .Select(line => new RunOutcomeLootLineDto { Kind = line.Kind, TypeId = line.TypeId, Quantity = line.Quantity })]
                })
        ]);
    }
}
