using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Runs.Commands;

/// <summary>Stamps the server a group code's fleet lives on onto its <c>RunGroupOrigin</c> (ET-245) — once, and only
/// on a group whose fleet is already recorded: a later answer never overwrites the first, the same rule the fleet id
/// itself follows.</summary>
public sealed record RecordRunGroupServerCommand(string GroupCode, string ServerAddress) : ICommand<Result>;
