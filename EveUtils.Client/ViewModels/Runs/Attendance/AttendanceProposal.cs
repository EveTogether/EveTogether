using System;
using System.Collections.Generic;
using System.Linq;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Client.ViewModels.Runs.Attendance;

/// <summary>
/// The preselection of a homefront's attendance list (ET-230) — a proposal the one who decides corrects, never a
/// verdict. Everyone in the fleet is in the site (Jithran, 2026-09-12: "als ik in een fleet zit ga ik er vanuit dat
/// fleetmembers meedoen"), externals included — they count for N. For each character, the first of these that knows
/// them:
/// <list type="number">
/// <item>The list this very run already stands at — a window reopened mid-run, or a commander taking over from another,
/// picks the decision up where it was rather than starting it over from what this window happened to see.</item>
/// <item>A character somebody deliberately took out of the previous homefront of the same fleet stays out, "as last
/// site" — the exception is remembered per fleet, the same as the own-character pick remembers it (ET-270).</item>
/// <item>Otherwise in the site, whatever the evidence says — evidence only ever names why. Only a Local character
/// this client sees logged out starts out of it.</item>
/// </list>
/// Evidence may put a character back in, never take one out. A line set by hand in this run's list is not the
/// proposal's to change at all.
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
                ({ } entry, _, null) => new AttendanceProposalLine(id, entry.IsInSite, entry.Reason, entry.ReasonAmount),
                ({ }, _, { } seen) => new AttendanceProposalLine(id, true, seen.Reason, seen.Amount),
                (null, { IsInSite: false, Reason: AttendanceReason.SetByHand or AttendanceReason.SameAsLastSite }, { } seen) =>
                    new AttendanceProposalLine(id, true, seen.Reason, seen.Amount, IsAddedToLastSite: true),
                (null, { IsInSite: false, Reason: AttendanceReason.SetByHand or AttendanceReason.SameAsLastSite }, null) =>
                    new AttendanceProposalLine(id, false, AttendanceReason.SameAsLastSite, null),
                (_, _, { } seen) => new AttendanceProposalLine(id, true, seen.Reason, seen.Amount),
                _ => new AttendanceProposalLine(id, !candidate.IsLoggedOut, AttendanceReason.NoActivityLogged, null)
            });
        }

        return lines;
    }
}
