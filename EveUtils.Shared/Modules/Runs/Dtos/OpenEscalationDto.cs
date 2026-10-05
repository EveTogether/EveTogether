namespace EveUtils.Shared.Modules.Runs.Dtos;

/// <summary>One escalation still to be flown (ET-451), with the run it came from — what the open-escalations list
/// shows and what starting the escalation run needs. <see cref="InProgressRunId"/> is the escalation run already
/// started for it and not yet saved, so the list offers to open that one instead of starting a second.</summary>
public sealed record OpenEscalationDto(
    Guid SourceRunId,
    long CharacterId,
    string? CharacterNameSnapshot,
    string? SourceSiteName,
    DateTime SourceStartedAtUtc,
    RunEscalationDto Escalation,
    Guid? InProgressRunId);
