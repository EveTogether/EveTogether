using System.Linq;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Messaging.Dtos;
using EveUtils.Shared.Modules.Messaging.Entities;
using EveUtils.Shared.Modules.Messaging.Repositories;

namespace EveUtils.Shared.Modules.Messaging.Commands;

internal sealed class RespondToMessageCommandHandler(IMessageRepository repository, IEnumerable<IMessageResponder> responders)
    : ICommandHandler<RespondToMessageCommand, Result<MessageResponsePayload>>
{
    public async Task<Result<MessageResponsePayload>> Handle(RespondToMessageCommand command, CancellationToken cancellationToken = default)
    {
        try
        {
            var message = await repository.GetAsync(command.MessageId, cancellationToken);
            if (message is null)
                return Result<MessageResponsePayload>.Failure(new ResultMessage(
                    MessageSeverity.Error, MessageCodes.NotFound, "Message not found.", "Messaging"));

            // Only the recipient may respond to their own message.
            if (message.RecipientCharacterId != command.ActingCharacterId)
                return Result<MessageResponsePayload>.Failure(new ResultMessage(
                    MessageSeverity.Error, MessageCodes.PermissionDenied, "Only the recipient can respond to this message.", "Messaging"));

            if (message.Status == MessageStatus.Responded)
                return Result<MessageResponsePayload>.Failure(new ResultMessage(
                    MessageSeverity.Error, MessageCodes.ValidationFailed, "Message already responded to.", "Messaging"));

            var responder = responders.FirstOrDefault(r => r.Kind == message.Kind);
            if (responder is null)
                return Result<MessageResponsePayload>.Failure(new ResultMessage(
                    MessageSeverity.Error, MessageCodes.ValidationFailed, "This message kind cannot be responded to.", "Messaging"));

            var outcome = await responder.RespondAsync(message, command.Accept, command.ActingCharacterId, cancellationToken);
            if (!outcome.IsSuccess)
                return Result<MessageResponsePayload>.Failure(outcome.Messages.ToArray());

            message.Status = MessageStatus.Responded;
            await repository.UpdateAsync(message, cancellationToken);

            return Result<MessageResponsePayload>.Success(new MessageResponsePayload(message.Id, message.Kind, command.Accept));
        }
        catch (Exception ex)
        {
            return Result<MessageResponsePayload>.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.ServerError, $"Failed to respond to message: {ex.Message}", "Messaging"));
        }
    }
}
