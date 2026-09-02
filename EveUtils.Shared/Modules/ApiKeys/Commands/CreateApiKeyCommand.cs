using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.ApiKeys.Dtos;

namespace EveUtils.Shared.Modules.ApiKeys.Commands;

/// <summary>
/// Mints an API key: generates it, stores only the prefix and the hash, and returns the plaintext once.
/// <see cref="OwnerCharacterId"/> null = admin scope over all server data.
/// </summary>
public sealed record CreateApiKeyCommand(
    string Label,
    IReadOnlyList<string> Scopes,
    string CreatedBy,
    int? OwnerCharacterId = null,
    DateTimeOffset? ExpiresAt = null) : ICommand<Result<NewApiKeyDto>>;
