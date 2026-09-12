using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Dtos;

/// <summary>One character on a homefront's attendance list (ET-230), in the one shape it travels in everywhere: the
/// fleet commander's <c>fleet.run-attendance</c>, <see cref="RunWireData"/> and what
/// <see cref="Entities.RunAttendanceEntry"/> stores.</summary>
public sealed class RunAttendanceEntryInput
{
    public required long CharacterId { get; init; }
    public string? CharacterName { get; init; }
    public required bool IsInSite { get; init; }
    public bool IsExternal { get; init; }
    public required AttendanceReason Reason { get; init; }
    public long? ReasonAmount { get; init; }
}
