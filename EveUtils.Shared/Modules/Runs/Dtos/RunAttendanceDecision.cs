using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Dtos;

/// <summary>
/// Who was in the site when a homefront completed, as one person decided it (ET-230): the list, the pilots on no
/// roster at all, and who decided when. The same decision lands on every run of the group, on every member's machine,
/// which is what makes <see cref="InSiteCount"/> — N, the figure the payout hangs on — the same everywhere.
/// </summary>
/// <param name="SetAtUtc">On the deciding client's clock. Only ever compared with another decision of the same group,
/// so a later correction replaces an earlier one and a late copy of the earlier one never comes back.</param>
public sealed record RunAttendanceDecision(
    IReadOnlyList<RunAttendanceEntryInput> Entries,
    int NotOnRosterCount,
    AttendanceSource Source,
    long SetByCharacterId,
    DateTime SetAtUtc)
{
    /// <summary>N: the ticked characters plus the pilots on no roster.</summary>
    public int InSiteCount => Entries.Count(entry => entry.IsInSite) + NotOnRosterCount;

    /// <summary>Whether the two say the same thing, apart from when and by whom — what lets a copy that arrives again
    /// (a resend, the same decision back from the server) leave the stored one alone.</summary>
    public bool ListsTheSameAs(RunAttendanceDecision other) =>
        NotOnRosterCount == other.NotOnRosterCount
        && Entries.Count == other.Entries.Count
        && Entries.OrderBy(entry => entry.CharacterId).Zip(other.Entries.OrderBy(entry => entry.CharacterId))
            .All(pair => pair.First.CharacterId == pair.Second.CharacterId
                         && pair.First.IsInSite == pair.Second.IsInSite
                         && pair.First.IsExternal == pair.Second.IsExternal
                         && pair.First.Reason == pair.Second.Reason
                         && pair.First.ReasonAmount == pair.Second.ReasonAmount
                         && pair.First.CharacterName == pair.Second.CharacterName);

    /// <summary>The decision a run carries, or null for a run nobody decided one for.</summary>
    public static RunAttendanceDecision? Of(Entities.Run run) =>
        run is { AttendanceSource: { } source, AttendanceSetByCharacterId: { } setBy, AttendanceSetAtUtc: { } setAt }
            ? new RunAttendanceDecision(
                [.. run.AttendanceEntries.Select(entry => new RunAttendanceEntryInput
                {
                    CharacterId = entry.CharacterId,
                    CharacterName = entry.CharacterName,
                    IsInSite = entry.IsInSite,
                    IsExternal = entry.IsExternal,
                    Reason = entry.Reason,
                    ReasonAmount = entry.ReasonAmount
                })],
                run.AttendanceNotOnRosterCount ?? 0, source, setBy, setAt)
            : null;
}
