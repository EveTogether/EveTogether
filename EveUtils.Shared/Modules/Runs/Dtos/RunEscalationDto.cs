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
    /// <summary>How long an escalation registered without a readable deadline stays flyable — past it nobody can still
    /// tell whether the site is there, so it leaves the open list like an expired one (ET-453).</summary>
    public static readonly TimeSpan UndatedLifetime = TimeSpan.FromHours(24);

    /// <summary>Still to be flown: nobody settled it and its deadline has not passed — or, without a deadline, it was
    /// registered less than <see cref="UndatedLifetime"/> ago.</summary>
    public bool IsOpenAt(DateTime nowUtc) =>
        Outcome is null && (ExpiresAtUtc ?? RegisteredAtUtc + UndatedLifetime) > nowUtc;

    public EscalationStanding StandingAt(DateTime nowUtc) => Outcome switch
    {
        EscalationOutcome.Completed => EscalationStanding.Done,
        null when IsOpenAt(nowUtc) => EscalationStanding.Open,
        _ => EscalationStanding.Missed
    };
}
