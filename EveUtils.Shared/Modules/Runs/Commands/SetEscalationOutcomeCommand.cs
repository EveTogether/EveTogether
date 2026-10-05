using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Commands;

/// <summary>
/// Tick one of a run's escalations off (ET-451): completed — by the run that flew it, or by hand — or expired, or
/// back to open. Writes the <see cref="RunParameterKey.EscalationOutcome"/> and
/// <see cref="RunParameterKey.EscalationCompletedByRunId"/> rows beside the escalation's own, replacing whatever
/// outcome it had.
/// </summary>
/// <param name="RunId">The source run — this client's own, the same rule every run-owning command follows.</param>
/// <param name="EntryId">Which of its escalations; null for one registered before escalations carried an entry id.</param>
/// <param name="Outcome">The new outcome, or null to reopen the escalation.</param>
/// <param name="CompletedByRunId">The escalation run that flew it, only with <see cref="EscalationOutcome.Completed"/>.</param>
public sealed record SetEscalationOutcomeCommand(
    Guid RunId, Guid? EntryId, EscalationOutcome? Outcome, Guid? CompletedByRunId = null) : ICommand<Result>;
