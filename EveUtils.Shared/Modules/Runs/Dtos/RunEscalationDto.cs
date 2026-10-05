using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Dtos;

/// <summary>One escalation a run led to, read back from its <c>Escalation*</c> parameter rows (ET-451). A run can
/// carry several; <see cref="EntryId"/> tells them apart and is null only for the one escalation a run registered
/// before entries existed.</summary>
public sealed record RunEscalationDto(
    Guid? EntryId,
    string SiteName,
    int? DungeonId,
    string? SystemName,
    int? SolarSystemId,
    DateTime? ExpiresAtUtc,
    DateTime RegisteredAtUtc,
    EscalationOutcome? Outcome,
    Guid? CompletedByRunId)
{
    /// <summary>Still to be flown: nobody ticked it off and its deadline has not passed. An escalation registered
    /// without a readable deadline stays open until someone says otherwise.</summary>
    public bool IsOpenAt(DateTime nowUtc) => Outcome is null && !(ExpiresAtUtc <= nowUtc);
}
