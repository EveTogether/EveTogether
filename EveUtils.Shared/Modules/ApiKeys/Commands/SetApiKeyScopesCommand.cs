using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.ApiKeys.Commands;

/// <summary>Replaces a key's scope set. An empty set leaves the key unable to reach any data route.</summary>
public sealed record SetApiKeyScopesCommand(int ApiKeyId, IReadOnlyList<string> Scopes) : ICommand<Result>;
