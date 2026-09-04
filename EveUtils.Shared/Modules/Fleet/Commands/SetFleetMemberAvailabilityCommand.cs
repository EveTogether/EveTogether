using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Entities;

namespace EveUtils.Shared.Modules.Fleet.Commands;

/// <summary>
/// Sets the pilot's own availability for a fleet that has not started yet (ET-169) — signing off without
/// leaving the roster, or reversing that. Self-only (like the fit verdict and in-game presence reports):
/// the acting character must BE the member, never the fleet's creator and never another member. This is the
/// first roster-affecting command that must reject its own creator when they are not also the target member —
/// every other roster command in this module is creator-only or creator-or-self; this one is self-only-full-stop.
/// </summary>
public sealed record SetFleetMemberAvailabilityCommand(
    long MemberId, FleetMemberAvailability Availability, string? Note, int ActingCharacterId) : ICommand<Result>;
