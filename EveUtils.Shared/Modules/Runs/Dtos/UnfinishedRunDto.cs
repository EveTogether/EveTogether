using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Dtos;

/// <param name="StoppedAtUtc">Null when the clock was never brought to rest — a row that reached
/// <see cref="Enums.RunState.Stopped"/> through a path that did not stamp it.</param>
/// <param name="TotalIsk">This run's own earnings so far, added up the same way <c>TotalIskCalculator</c> adds up a
/// saved activity's or a live window's (ET-217): bounty, priced loot net of what was lost, and ISK-shaped rewards.
/// Bounty reads zero here — a gamelog payout is only written to storage at SAVE, and this run has not been saved —
/// which is a gap in what is persisted before save, not a second formula.</param>
public sealed record UnfinishedRunDto(
    Guid RunId,
    long CharacterId,
    ActivityKind ActivityKind,
    string? SiteName,
    DateTime StartedAtUtc,
    DateTime? StoppedAtUtc,
    decimal TotalIsk);
