using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;

namespace EveUtils.Shared.Modules.Fleet.Commands;

/// <summary>
/// A character asks to join an invite-only fleet — the mirror of <see cref="CreateFleetInviteCommand"/>.
/// No app-permission gate: requesting is the acting character's own action. The handler validates the fleet is
/// active and invite-only (a public fleet is joined directly), that the requester is not already a member and has
/// no duplicate pending request, persists a Pending request and enqueues a message to the fleet owner.
/// </summary>
public sealed record RequestToJoinCommand(long FleetId, int ActingCharacterId) : ICommand<Result<FleetJoinRequestPayload>>;
