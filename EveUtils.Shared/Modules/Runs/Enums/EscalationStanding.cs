namespace EveUtils.Shared.Modules.Runs.Enums;

/// <summary>Where an escalation stands, as the runs overview colours it (ET-453) — derived at read time from its
/// outcome, deadline and registration moment, never stored.</summary>
public enum EscalationStanding
{
    Open,
    Done,
    /// <summary>Expired, declined, or past what could still be flown.</summary>
    Missed
}
