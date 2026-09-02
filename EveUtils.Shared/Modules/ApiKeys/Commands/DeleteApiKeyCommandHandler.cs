using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.ApiKeys.Repositories;

namespace EveUtils.Shared.Modules.ApiKeys.Commands;

internal sealed class DeleteApiKeyCommandHandler(IApiKeyRepository repository)
    : ICommandHandler<DeleteApiKeyCommand, Result>
{
    public async Task<Result> Handle(DeleteApiKeyCommand command, CancellationToken cancellationToken = default) =>
        await repository.DeleteAsync(command.ApiKeyId, cancellationToken)
            ? Result.Success()
            : Result.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.NotFound, "API key not found.", "ApiKey"));
}
