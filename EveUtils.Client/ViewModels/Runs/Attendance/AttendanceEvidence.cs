using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Client.ViewModels.Runs.Attendance;

/// <summary>The strongest thing a character is seen doing to the site this run (ET-230) — enough to propose them as
/// in the site, never to decide it.</summary>
/// <param name="Amount">The figure behind it, or null where the reason has none.</param>
public sealed record AttendanceEvidence(AttendanceReason Reason, long? Amount);
