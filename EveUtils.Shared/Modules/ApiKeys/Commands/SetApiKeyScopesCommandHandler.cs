using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.ApiKeys.Repositories;
using EveUtils.Shared.Modules.ApiKeys.Services;

namespace EveUtils.Shared.Modules.ApiKeys.Commands;

internal sealed class SetApiKeyScopesCommandHandler(IApiKeyRepository repository)
    : ICommandHandler<SetApiKeyScopesCommand, Result>
{
    public async Task<Result> Handle(SetApiKeyScopesCommand command, CancellationToken cancellationToken = default)
    {
        var unknown = command.Scopes.Where(scope => !ApiKeyScopes.All.Contains(scope)).ToList();
        if (unknown.Count > 0)
            return Result.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.ValidationFailed,
                $"Unknown scope(s): {string.Join(", ", unknown)}.", "ApiKey"));

        return await repository.SetScopesAsync(command.ApiKeyId, string.Join(',', command.Scopes), cancellationToken)
            ? Result.Success()
            : Result.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.NotFound, "API key not found.", "ApiKey"));
    }
}
