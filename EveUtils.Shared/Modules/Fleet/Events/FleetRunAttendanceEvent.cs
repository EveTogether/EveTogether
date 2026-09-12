using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;

namespace EveUtils.Shared.Modules.Fleet.Events;

/// <summary>
/// The fleet commander's attendance list for a homefront (ET-230). Fleet-scoped like the other <c>fleet.run-*</c>
/// events, so the server reroutes it to the fleet's connected members, and each applies it to its own runs only.
///
/// A client or server that predates it has no deserializer for its type and drops it unread: such a member keeps no
/// list until it pulls the commander's runs, which carry the same decision.
/// </summary>
public sealed class FleetRunAttendanceEvent(RunGroupAttendance data, int? characterId = null)
    : IntegrationEvent<RunGroupAttendance>(data, characterId), IFleetScopedEvent
{
    public override string EventType => "fleet.run-attendance";

    public long FleetId => Data.FleetId;
}
