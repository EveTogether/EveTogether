using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.ApiKeys.Commands;

/// <summary>Switches a key off without deleting it, so the audit trail (prefix, label, last-used) survives.</summary>
public sealed record RevokeApiKeyCommand(int ApiKeyId) : ICommand<Result>;
