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
public sealed record DeleteRunsInGroupCommand(string GroupCode, DateTime DeletedAtUtc) : ICommand<Result<int>>;
