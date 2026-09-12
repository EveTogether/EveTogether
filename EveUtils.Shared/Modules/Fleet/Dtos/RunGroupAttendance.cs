using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Fleet.Dtos;

/// <summary>
/// The fleet commander's attendance list for a homefront (ET-230), and how it ended (ET-231): every character on the
/// roster with a tick for "in site at completion" and why, the pilots on no roster at all, and the outcome. The wire
/// payload of <see cref="Events.FleetRunAttendanceEvent"/>; the commander is the envelope's own character, never a
/// field of this.
///
/// The whole list every time rather than a change, for the reason <see cref="RunShareUpdate"/> gives: a member whose
/// client connected late has everything with the next one, and no order two messages arrive in leaves a wrong list
/// standing. <see cref="UnixMs"/> is when the commander made it, which is what lets a receiver keep the newest.
/// Carries the group code rather than a run id, like <see cref="RunGroupStop"/>: every member's run under it is their
/// own.
/// </summary>
public sealed record RunGroupAttendance(
    long FleetId,
    string GroupCode,
    long UnixMs,
    IReadOnlyList<RunAttendanceEntryInput> Characters,
    int NotOnRosterCount,
    HomefrontOutcome? Outcome = null,
    int? CompletedWaveCount = null)
{
    /// <summary>An instant cut to whole milliseconds, the grain <see cref="UnixMs"/> carries — what the commander stamps
    /// their own copy with, so theirs and every member's are the same instant and a resend reads as the same list.</summary>
    public static DateTime ToWireInstant(DateTime utc) =>
        new(utc.Ticks - utc.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);

    public static long ToUnixMs(DateTime utc) => new DateTimeOffset(ToWireInstant(utc), TimeSpan.Zero).ToUnixTimeMilliseconds();
}
