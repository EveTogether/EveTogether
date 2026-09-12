using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Dtos;

namespace EveUtils.Shared.Modules.Runs.Commands;

/// <summary>
/// Write a homefront's attendance decision onto this client's own runs (ET-230): the list, N and who decided when, and
/// on each run its own character's tick. Addressed by the group's code, or by the one run of a pilot flying alone.
///
/// Only runs of <see cref="OwnCharacterIds"/> are touched — a group-mate's run pulled from a server lies in the same
/// store under the same code, and no machine ever writes another pilot's data (the rule
/// <c>FleetRunGroupCodeCoordinator</c> already applies to a discard). A decision older than the one a run already
/// carries is left out, so a late or resent copy never takes a correction back — and a pilot's own decision never
/// replaces the fleet commander's.
/// </summary>
/// <returns>How many runs took the decision; 0 when every run already carried it or a newer one.</returns>
public sealed record SetRunAttendanceCommand(
    RunAttendanceDecision Decision,
    IReadOnlyCollection<long> OwnCharacterIds,
    string? GroupCode = null,
    Guid? RunId = null) : ICommand<Result<int>>;
