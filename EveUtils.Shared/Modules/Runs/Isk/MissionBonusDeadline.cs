namespace EveUtils.Shared.Modules.Runs.Isk;

/// <summary>
/// The one rule for when a mission's bonus stops being earned (ET-237, ET-256), read by the rewards contributor and
/// by both MISSION sections. The capture states the time "remaining" at the moment it was copied, so the deadline is
/// that moment plus the stated window. Judged at STOP once a run has stopped: a bonus met before STOP stays earned
/// however long the run then sits unsaved.
/// </summary>
public static class MissionBonusDeadline
{
    public static DateTime? Of(int? bonusWindowSeconds, DateTime observedAtUtc) =>
        bonusWindowSeconds is { } seconds ? observedAtUtc.AddSeconds(seconds) : null;

    public static bool HasPassed(int? bonusWindowSeconds, DateTime observedAtUtc, DateTime judgedAtUtc) =>
        Of(bonusWindowSeconds, observedAtUtc) is { } deadline && judgedAtUtc >= deadline;
}
