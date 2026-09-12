using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Entities;

/// <summary>One character on the attendance list a homefront's run was decided with (ET-230) — every character on the
/// fleet's roster, not only this run's own, so the run can still show the whole list after the fleet is gone. The run's own
/// verdict is <see cref="Run.InSiteAtCompletion"/>; this is the list it was read from.</summary>
public sealed class RunAttendanceEntry
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }

    /// <summary>Any character on the roster, so not necessarily a character that has a run in this database.</summary>
    public long CharacterId { get; set; }

    /// <summary>The name as the one who decided knew it — the same snapshot reasoning as
    /// <see cref="Run.CharacterNameSnapshot"/>: an external pilot is never resolvable anywhere else.</summary>
    public string? CharacterName { get; set; }

    public bool IsInSite { get; set; }

    /// <summary>On the roster without an Eve Together client (FleetMember.IsExternal), so no evidence could ever arrive
    /// for them.</summary>
    public bool IsExternal { get; set; }

    public AttendanceReason Reason { get; set; }

    /// <summary>The figure behind <see cref="Reason"/> — damage, repair, capacitor, units mined, wrecks salvaged — or
    /// null where the reason has none.</summary>
    public long? ReasonAmount { get; set; }

    public Run? Run { get; set; }
}
