using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Fleet.Commands;

internal sealed class RespondToJoinRequestCommandHandler(JoinRequestResponder responder)
    : ICommandHandler<RespondToJoinRequestCommand, Result>
{
    public Task<Result> Handle(RespondToJoinRequestCommand command, CancellationToken cancellationToken = default) =>
        responder.RespondAsync(command.RequestId, command.Accept, command.ActingCharacterId, cancellationToken);
}
