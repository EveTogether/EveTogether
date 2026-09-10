using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Runs.Commands;

/// <summary>
/// Apply a discard to every run sharing a group code on this client. Only ever reaches the runs in this client's own
/// database — which are this pilot's — so "discard fans out to five machines" stays five pilots each ending their
/// own run, not one pilot reaching into four others' data. Returns how many runs were discarded here.
/// </summary>
/// <param name="DeleteAfterDiscard">Whether a run this reaches that was not yet saved should be soft-deleted on top
/// of being stopped and unlinked (ET-220), the same choice <see cref="DiscardRunCommand.DeleteAfterDiscard"/> makes
/// for a single run. This command carries two different callers under one type, and they must not share this
/// answer: the pilot's own ET-210 multi-toon group, discarded on their own machine by their own choice, should throw
/// every one of those own runs away — but the very same command also arrives here as the fanout of a fleet
/// commander's discard (the client's FleetRunGroupCodeCoordinator sends this in response to
/// EveUtils.Shared.Modules.Fleet.Events.FleetRunDiscardedEvent), and a member's own unfinished run must survive that
/// untouched (ET-105 AC-1): ending the shared activity is the commander's call, throwing a member's own registration
/// away is only theirs to make. False is the fanout's answer; only the pilot's own direct call passes true.</param>
public sealed record DiscardRunsInGroupCommand(
    string GroupCode, DateTime DiscardedAtUtc, bool DeleteAfterDiscard = false) : ICommand<Result<int>>;
