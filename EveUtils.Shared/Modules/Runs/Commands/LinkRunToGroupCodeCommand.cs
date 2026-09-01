using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Runs.Commands;

public sealed record LinkRunToGroupCodeCommand(Guid RunId, string GroupCode) : ICommand<Result>;
