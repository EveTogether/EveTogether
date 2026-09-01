using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Runs.Commands;

public sealed record DeleteRunCommand(Guid RunId, DateTime DeletedAtUtc) : ICommand<Result>;
