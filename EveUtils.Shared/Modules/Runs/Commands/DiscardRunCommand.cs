using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Runs.Commands;

/// <summary>
/// End this pilot's part in a run. There is no group entity to delete (ET-104): discard stops the activity and
/// unlinks the group code, keeping the former code as an audit value. It never removes a row, a loot capture or a
/// bounty of a run that was already saved — a member who already saved keeps their run intact as a standalone one
/// (ET-105 AC-1).
/// </summary>
/// <param name="DeleteAfterDiscard">Whether a run this reaches that was not yet saved should be soft-deleted on top
/// of being stopped and unlinked (ET-220) — the pilot is not merely ending the activity, they are throwing their own
/// unfinished registration away. False for every fanout of someone else's decision (a fleet commander ending a
/// shared run reaches a member's client through <see cref="DiscardRunsInGroupCommand"/>, never this command), so the
/// default keeps a plain discard exactly as unlinking-only as it always was.</param>
public sealed record DiscardRunCommand(Guid RunId, DateTime StoppedAtUtc, bool DeleteAfterDiscard = false)
    : ICommand<Result>;
