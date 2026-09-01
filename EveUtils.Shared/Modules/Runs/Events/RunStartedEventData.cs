using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Events;

public sealed record RunStartedEventData(
    Guid RunId,
    long CharacterId,
    ActivityKind ActivityKind,
    DateTime StartedAtUtc,
    long? FleetId,
    string? GroupCode,
    bool IsFleetCommander);
