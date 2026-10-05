namespace EveUtils.Shared.Modules.Runs.Enums;

/// <summary>How a registered escalation ended (ET-451). Stored by value in a <see cref="Entities.RunParameter"/>, so
/// members are only ever appended. An escalation with no outcome is still open.</summary>
public enum EscalationOutcome
{
    Completed,
    Expired
}
