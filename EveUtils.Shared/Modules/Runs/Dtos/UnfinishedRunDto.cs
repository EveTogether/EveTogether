using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Dtos;

/// <param name="StoppedAtUtc">Null when the clock was never brought to rest — a row that reached
/// <see cref="Enums.RunState.Stopped"/> through a path that did not stamp it.</param>
public sealed record UnfinishedRunDto(
    Guid RunId,
    long CharacterId,
    ActivityKind ActivityKind,
    string? SiteName,
    DateTime StartedAtUtc,
    DateTime? StoppedAtUtc);
