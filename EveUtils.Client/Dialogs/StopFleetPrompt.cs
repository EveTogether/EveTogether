using System;
using System.Collections.Generic;

namespace EveUtils.Client.Dialogs;

/// <summary>
/// What the stop dialog is told about the fleet it is asked to end (ET-166). A carrier: the roster window has all
/// of this loaded already, and the dialog shows it back so the FC decides with the fleet's actual state in front of
/// them rather than from the name alone.
/// </summary>
/// <param name="FleetName">The fleet, named in the header chip.</param>
/// <param name="ActivatedAt">When it was started, so the dialog can say how long it has been running; null when
/// unknown (an older fleet started before the server recorded it).</param>
/// <param name="OwnMemberCount">How many of my own characters are on the roster.</param>
/// <param name="OtherMemberCount">Roster members who are neither mine nor external.</param>
/// <param name="ExternalMemberCount">External members — added on trust, no session of their own.</param>
/// <param name="RunsInProgress">This client's runs still going in this fleet, as
/// <c>"Kaska Vex — Fortress Sansha, 00:11:42"</c> lines; empty when nothing is running.</param>
/// <param name="LeavableCharacterCount">How many of my characters could leave on their own instead of the whole
/// fleet stopping. Zero hides that option — the owner's own character is never a candidate.</param>
/// <param name="CompletedRunCount">How many of this fleet's runs are known to be completed (ET-185, via
/// <c>GetFleetRunCoverageQuery</c>); null when that is not knowable rather than zero — a fleet older than
/// <c>RunGroupOrigin</c> (ET-182) can look empty for a reason that has nothing to do with what it flew. The dialog
/// only ever prints a number here when it is this: never a guess standing in for "unknown".</param>
public sealed record StopFleetPrompt(
    string FleetName,
    DateTimeOffset? ActivatedAt,
    int OwnMemberCount,
    int OtherMemberCount,
    int ExternalMemberCount,
    IReadOnlyList<string> RunsInProgress,
    int LeavableCharacterCount,
    int? CompletedRunCount = null);
