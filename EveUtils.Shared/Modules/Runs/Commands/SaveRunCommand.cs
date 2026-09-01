using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Dtos;

namespace EveUtils.Shared.Modules.Runs.Commands;

public sealed record SaveRunCommand(
    Guid RunId,
    DateTime StoppedAtUtc,
    DateTime SavedAtUtc,
    IReadOnlyList<RunLootCaptureInput> LootCaptures,
    IReadOnlyList<RunBountyEntryInput> BountyEntries,
    IReadOnlyList<RunEnemyObservationInput> EnemyObservations,
    IReadOnlyList<RunParameterInput> Parameters) : ICommand<Result>;
