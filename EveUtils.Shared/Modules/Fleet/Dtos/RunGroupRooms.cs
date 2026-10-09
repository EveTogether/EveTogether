using EveUtils.Shared.Modules.Gamelog.Aggregation;

namespace EveUtils.Shared.Modules.Fleet.Dtos;

/// <summary>One room boundary of a shared abyssal run (ET-494): when it began, and how sure the detector was — null for
/// one the commander set by hand.</summary>
public sealed record RunGroupRoom(DateTime AtUtc, RoomCertainty? Certainty);

/// <summary>The fleet commander's whole list of room boundaries for the run under <see cref="GroupCode"/> (ET-494) — the
/// whole state rather than a change, so an undo, a late join and a message out of order all land the same.</summary>
public sealed record RunGroupRooms(long FleetId, string GroupCode, IReadOnlyList<RunGroupRoom> Rooms);

/// <summary>A room a fleet member's own game log found (ET-494), offered to the commander, who decides.</summary>
public sealed record RunGroupRoomProposal(long FleetId, string GroupCode, DateTime AtUtc, RoomCertainty Certainty);
