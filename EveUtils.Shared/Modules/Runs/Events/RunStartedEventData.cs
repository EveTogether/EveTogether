using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Events;

public sealed record RunStartedEventData(
    Guid RunId,
    long CharacterId,
    ActivityKind ActivityKind,
    DateTime StartedAtUtc,
    long? FleetId,
    string? GroupCode,
    bool IsFleetCommander,
    // Where this run is being flown. Not stored on the row — the run keeps SolarSystemId and SiteName — but the
    // fleet coordinator needs both to tell one group from another, and this is its only sight of a local start.
    string? SolarSystemName = null,
    string? SiteName = null);
