using System;
using System.Collections.Generic;
using System.Linq;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Client.ViewModels.Runs.Attendance;

/// <summary>
/// The preselection of a homefront's attendance list (ET-230) — a proposal the one who decides corrects, never a
/// verdict. For each character, the first of these that knows them:
/// <list type="number">
/// <item>The list this very run already stands at — a window reopened mid-run, or a commander taking over from another,
/// picks the decision up where it was rather than starting it over from what this window happened to see.</item>
/// <item>The list the previous homefront of the same fleet ended with (Jithran, 2026-09-11: a series keeps its list),
/// with "same as last site" as the reason.</item>
/// <item>Evidence from this run alone. No evidence at all defaults an own character in — deliberately put into the
/// run, they are assumed present until shown otherwise (Jithran, 2026-09-12: a hauler with no evidence tracker
/// catches, such as delivering cargo, still counted as flying the site) — and defaults anyone else, external or not,
/// unticked: nothing this client can vouch for.</item>
/// </list>
/// In the first two, evidence may add a tick, never take one away: a pilot who sat still on site 2 was still on site
/// 1's list for a reason. A line set by hand in this run's list is not the proposal's to change at all.
/// </summary>
public static class AttendanceProposal
{
    public static IReadOnlyList<AttendanceProposalLine> Propose(
        IReadOnlyList<AttendanceCandidate> candidates,
        Func<long, AttendanceEvidence?> evidenceOf,
        RunAttendanceDecision? lastSite,
        RunAttendanceDecision? standing = null)
    {
        List<AttendanceProposalLine> lines = [];
        foreach (AttendanceCandidate candidate in candidates)
        {
            long id = candidate.CharacterId;
            AttendanceEvidence? evidence = evidenceOf(id);
            RunAttendanceEntryInput? stands = standing?.Entries.FirstOrDefault(entry => entry.CharacterId == id);
            RunAttendanceEntryInput? carried = lastSite?.Entries.FirstOrDefault(entry => entry.CharacterId == id);

            lines.Add((stands, carried, evidence) switch
            {
                ({ Reason: AttendanceReason.SetByHand } entry, _, _) =>
                    new AttendanceProposalLine(id, entry.IsInSite, AttendanceReason.SetByHand, null),
                ({ IsInSite: true } entry, _, null) => new AttendanceProposalLine(id, true, entry.Reason, entry.ReasonAmount),
                ({ IsInSite: false } entry, _, null) => new AttendanceProposalLine(id, false, entry.Reason, entry.ReasonAmount),
                ({ }, _, { } seen) => new AttendanceProposalLine(id, true, seen.Reason, seen.Amount),
                (null, { IsInSite: true }, null) => new AttendanceProposalLine(id, true, AttendanceReason.SameAsLastSite, null),
                (null, { IsInSite: false }, { } seen) =>
                    new AttendanceProposalLine(id, true, seen.Reason, seen.Amount, IsAddedToLastSite: true),
                (_, _, { } seen) => new AttendanceProposalLine(id, true, seen.Reason, seen.Amount),
                // Nothing to go on at all: an own character deliberately put into the run is assumed present until
                // shown otherwise (ET-269); anyone else — external or merely another pilot on the roster — is not.
                _ => new AttendanceProposalLine(id, candidate.IsLocal, AttendanceReason.NoActivityLogged, null)
            });
        }

        return lines;
    }
}
