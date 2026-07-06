using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Fleet.Commands;

/// <summary>
/// Couples a reusable composition (doctrine) to a fleet, or unlinks it when <see cref="CompositionId"/> is null
/// . Creator-only and only while the fleet is still Forming — the doctrine is chosen before the fleet
/// starts. One composition can back many fleets (Fleet.FleetCompositionId, not the other way round).
/// </summary>
public sealed record SetFleetCompositionCommand(long FleetId, long? CompositionId, int ActingCharacterId) : ICommand<Result>;
