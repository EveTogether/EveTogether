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
    IReadOnlyList<RunParameterInput> Parameters,
    /// <summary>A start corrected by hand before saving (ET-98), or null to keep the one the run was started with.
    /// The pilot presses START once the fight is already going, and this is where that slack is taken back out.</summary>
    DateTime? StartedAtUtc = null,
    /// <summary>Set when either time was corrected by hand, so the stored run keeps saying so. The caller says it
    /// rather than the handler working it out: a corrected stop is indistinguishable from a measured one by the
    /// time it arrives here, and a fact nobody records is a fact nobody can recover later.</summary>
    DateTime? TimesCorrectedAtUtc = null) : ICommand<Result>;
