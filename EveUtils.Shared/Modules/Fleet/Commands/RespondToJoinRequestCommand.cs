using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Fleet.Commands;

/// <summary>
/// The fleet owner answers a request-to-join directly by its request id — the command sibling of the
/// message-driven path (<c>RespondToMessage</c> → <c>FleetJoinRequestResponder</c>). No app-permission gate:
/// answering is the owner's own action, authorized as "acting character == fleet creator" inside the shared
/// <see cref="EveUtils.Shared.Modules.Fleet.JoinRequestResponder"/> the handler calls.
/// </summary>
public sealed record RespondToJoinRequestCommand(long RequestId, bool Accept, int ActingCharacterId) : ICommand<Result>;
