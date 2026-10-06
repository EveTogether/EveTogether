namespace EveUtils.Shared.Modules.Runs.Enums;

/// <summary>How a registered escalation ended (ET-451). Stored by value in a <see cref="Entities.RunParameter"/>, so
/// members are only ever appended. An escalation with no outcome is still open.</summary>
public enum EscalationOutcome
{
    Completed,
    Expired,
    /// <summary>The pilot chose not to fly it (ET-453) — "Won't do" in the open escalations list.</summary>
    Declined
}
