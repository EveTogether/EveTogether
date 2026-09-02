using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.ApiKeys.Dtos;
using EveUtils.Shared.Modules.ApiKeys.Entities;
using EveUtils.Shared.Modules.ApiKeys.Repositories;
using EveUtils.Shared.Modules.ApiKeys.Services;

namespace EveUtils.Shared.Modules.ApiKeys.Commands;

internal sealed class CreateApiKeyCommandHandler(IApiKeyRepository repository)
    : ICommandHandler<CreateApiKeyCommand, Result<NewApiKeyDto>>
{
    public async Task<Result<NewApiKeyDto>> Handle(CreateApiKeyCommand command, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command.Label))
            return Result<NewApiKeyDto>.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.ValidationFailed, "A label is required.", "ApiKey"));

        var unknown = command.Scopes.Where(scope => !ApiKeyScopes.All.Contains(scope)).ToList();
        if (unknown.Count > 0)
            return Result<NewApiKeyDto>.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.ValidationFailed,
                $"Unknown scope(s): {string.Join(", ", unknown)}.", "ApiKey"));

        GeneratedApiKey generated = ApiKeySecurity.Generate();
        var id = await repository.AddAsync(new ApiKey
        {
            Label = command.Label.Trim(),
            Prefix = generated.Prefix,
            SecretHash = ApiKeySecurity.Hash(generated.Secret),
            Scopes = string.Join(',', command.Scopes),
            OwnerCharacterId = command.OwnerCharacterId,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBy = command.CreatedBy,
            ExpiresAt = command.ExpiresAt
        }, cancellationToken);

        return Result<NewApiKeyDto>.Success(new NewApiKeyDto(id, generated.Prefix, generated.PlainText));
    }
}
