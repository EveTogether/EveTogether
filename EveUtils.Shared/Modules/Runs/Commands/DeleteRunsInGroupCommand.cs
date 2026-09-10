using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Runs.Commands;

/// <summary>
/// Soft-deletes every saved run of one activity's group in a single statement (ET-214) — an activity shown as one
/// row in the overview is a group of runs since ET-210, and taking one run out while leaving the rest would leave
/// the activity with a gap rather than gone. Mirrors <see cref="DeleteRunCommand"/>'s bulk update rather than
/// <see cref="DiscardRunsInGroupCommand"/>'s load-and-loop: nothing here needs a tracked <c>Run</c>, and there is
/// no reason to touch anything but <c>Run</c>'s own soft-delete columns. Returns how many runs were deleted.
/// </summary>
/// <param name="OnlyRunIds">Restricts the delete to these runs within the group, leaving the rest of the group
/// untouched; null deletes every saved run in the group. A fleetmate's run pulled from a server (ET-215) is shown
/// read-only on the detail screen for the same reason it must never land in here: a local soft delete flips
/// <c>SyncState</c> to <c>Pending</c>, and a run under someone else's character id sitting Pending would be this
/// machine offering to push a change to a run it does not own — exactly the "delete something at someone else's
/// place unnoticed" the ET-214 ticket warns against. The caller is expected to pass only its own characters' run
/// ids whenever the group may hold a foreign one.</param>
public sealed record DeleteRunsInGroupCommand(
    string GroupCode, DateTime DeletedAtUtc, IReadOnlyCollection<Guid>? OnlyRunIds = null) : ICommand<Result<int>>;
