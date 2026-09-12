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
///
/// The rules a homefront's money rests on (ET-271), and where each is kept:
/// <list type="number">
/// <item>I1 — one truth: an outcome or a tick is stored on every own run of the group the moment it is made, during the
/// run or after SAVE; no screen holds one only in memory (<c>HomefrontWindowSectionViewModel</c> writes a click at
/// once and flushes before SAVE).</item>
/// <item>I2/I3 — every total is the ISK registry's sum of what is stored, and a saved activity's summary is added up
/// again by this command whenever it changed one of its runs (so after STOP it equals what STOP showed).</item>
/// <item>I4 — an outcome, once set, is only ever replaced by another outcome, never erased by a list carrying none
/// (<see cref="RunAttendanceDecision.KeepingOutcomeOf"/>); the startup rebuild only re-derives from stored runs.</item>
/// <item>I5 — the newest decision wins whatever order copies arrive in; I6 — the same decision again changes nothing,
/// and a sibling run is added once per character.</item>
/// </list>
/// </summary>
/// <param name="IsProposal">A list the run window worked out by itself — evidence arriving, a roster read — rather than
/// one somebody clicked: written only while the store still holds <paramref name="StandingSetAtUtc"/>'s list, so a newer
/// decision made anywhere else (another window, the detail screen, the commander) is never overwritten by a proposal (I5).</param>
/// <param name="StandingSetAtUtc">For a proposal: when the stored list it was worked out from was set, or null for none.</param>
/// <returns>How many runs took the decision; 0 when every run already carried it or a newer one.</returns>
public sealed record SetRunAttendanceCommand(
    RunAttendanceDecision Decision,
    IReadOnlyCollection<long> OwnCharacterIds,
    string? GroupCode = null,
    Guid? RunId = null,
    bool IsProposal = false,
    DateTime? StandingSetAtUtc = null) : ICommand<Result<int>>;
