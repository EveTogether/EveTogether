using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.RunOutcomes.Enums;

namespace EveUtils.Shared.Modules.RunOutcomes.Commands;

/// <summary>
/// Removes the outcome of an activity its sharer deleted or discarded (the tombstone, ET-514), with its loot lines.
/// Only the contributor who stored it can, and only with a revision at least as new. Not gated by the server's switch:
/// taking one's own data back always works.
/// </summary>
public sealed record RetractRunOutcomeCommand(string ActivityId, string ContributorBucket, long Revision)
    : ICommand<Result<RunOutcomeShareStatus>>;
