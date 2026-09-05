using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Runs.Commands;

/// <param name="FleetId">The fleet this code belongs to, when the caller knows it — the arbiter's reconciliation
/// always does (ET-182). Null for a caller that only means to move a run between codes without asserting a fleet,
/// which leaves any existing <c>RunGroupOrigin</c> row exactly as it was.</param>
public sealed record LinkRunToGroupCodeCommand(Guid RunId, string GroupCode, long? FleetId = null) : ICommand<Result>;
