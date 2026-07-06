using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Messaging.Dtos;

namespace EveUtils.Shared.Modules.Messaging.Commands;

/// <summary>
/// The recipient accepts or declines a message that carries a response — generalizes
/// <c>RespondToFleetInviteCommand</c>. No app-permission gate: responding is the recipient's own action,
/// authorized as "acting character == recipient" in the handler. The handler delegates the domain action to
/// the registered <see cref="IMessageResponder"/> for the message kind, then marks the message Responded.
/// </summary>
public sealed record RespondToMessageCommand(long MessageId, bool Accept, int ActingCharacterId)
    : ICommand<Result<MessageResponsePayload>>;
