using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Fleet.Commands;

/// <summary>
/// A connected character joins a Public fleet directly. No app-permission gate — joining
/// a publicly listed fleet is the joiner's own action; the fleet's Public visibility is the gate. Invite-only
/// fleets are joined via an invite, not this command.
/// </summary>
public sealed record JoinFleetCommand(long FleetId, int ActingCharacterId) : ICommand<Result>;
