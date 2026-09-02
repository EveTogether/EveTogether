using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.ApiKeys.Commands;

/// <summary>Removes the key row outright — the harder counterpart of revoking.</summary>
public sealed record DeleteApiKeyCommand(int ApiKeyId) : ICommand<Result>;
