using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.ApiKeys.Repositories;

namespace EveUtils.Shared.Modules.ApiKeys.Commands;

internal sealed class RevokeApiKeyCommandHandler(IApiKeyRepository repository)
    : ICommandHandler<RevokeApiKeyCommand, Result>
{
    public async Task<Result> Handle(RevokeApiKeyCommand command, CancellationToken cancellationToken = default) =>
        await repository.SetActiveAsync(command.ApiKeyId, isActive: false, cancellationToken)
            ? Result.Success()
            : Result.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.NotFound, "API key not found.", "ApiKey"));
}
