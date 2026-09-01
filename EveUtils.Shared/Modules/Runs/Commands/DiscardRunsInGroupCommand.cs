using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Runs.Commands;

/// <summary>
/// Apply the fleet commander's discard on this client. Only ever reaches the runs in this client's own database —
/// which are this pilot's — so "discard fans out to five machines" stays five pilots each ending their own run,
/// not one pilot reaching into four others' data. Returns how many runs were discarded here.
/// </summary>
public sealed record DiscardRunsInGroupCommand(string GroupCode, DateTime DiscardedAtUtc) : ICommand<Result<int>>;
