using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Fleet.Commands;

/// <summary>
/// Adds one of the owner's own characters to their client-only fleet as an ordinary (non-external) roster member,
/// dropped into the first squad with room (EVE parity). The owner vouches for their own character: there is no remote
/// session to join from. Creator-only on a still-active fleet. Returns the new roster member's id.
/// </summary>
public sealed record AddLocalCharacterCommand(long FleetId, int CharacterId, int ActingCharacterId) : ICommand<Result<long>>;
