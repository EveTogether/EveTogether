using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Dtos;

/// <summary>
/// Who was in the site when a homefront completed, as one person decided it (ET-230), and how it ended (ET-231): the
/// list, the pilots on no roster at all, the outcome, and who decided when. The same decision lands on every run of
/// the group, on every member's machine, which is what makes <see cref="InSiteCount"/> — N, the figure the payout
/// hangs on — the same everywhere.
/// </summary>
/// <param name="Outcome">How the site ended — null while nobody has said. Not used for Abyssal Artifact Recovery,
/// which carries <paramref name="CompletedWaveCount"/> instead.</param>
/// <param name="CompletedWaveCount">Abyssal Artifact Recovery's own outcome: how many of its 9 waves paid out.
/// Null on every other kind, and on an AAR site nobody has said this for yet.</param>
/// <param name="SetAtUtc">On the deciding client's clock. Only ever compared with another decision of the same group,
/// so a later correction replaces an earlier one and a late copy of the earlier one never comes back.</param>
/// <param name="OutcomeFromGameLog">Whether <paramref name="Outcome"/> came from the Metaliminal Meteoroid "pale
/// shadow" gamelog line rather than a manual pick (ET-262) — never true together with a null
/// <paramref name="Outcome"/>, and always false once a manual pick replaces it.</param>
public sealed record RunAttendanceDecision(
    IReadOnlyList<RunAttendanceEntryInput> Entries,
    int NotOnRosterCount,
    AttendanceSource Source,
    long SetByCharacterId,
    DateTime SetAtUtc,
    HomefrontOutcome? Outcome = null,
    int? CompletedWaveCount = null,
    bool OutcomeFromGameLog = false)
{
    /// <summary>N: the ticked characters plus the pilots on no roster.</summary>
    public int InSiteCount => Entries.Count(entry => entry.IsInSite) + NotOnRosterCount;

    /// <summary>Whether the two say the same thing, apart from when and by whom — what lets a copy that arrives again
    /// (a resend, the same decision back from the server) leave the stored one alone.</summary>
    public bool ListsTheSameAs(RunAttendanceDecision other) =>
        NotOnRosterCount == other.NotOnRosterCount
        && Outcome == other.Outcome
        && CompletedWaveCount == other.CompletedWaveCount
        && OutcomeFromGameLog == other.OutcomeFromGameLog
        && Entries.Count == other.Entries.Count
        && Entries.OrderBy(entry => entry.CharacterId).Zip(other.Entries.OrderBy(entry => entry.CharacterId))
            .All(pair => pair.First.CharacterId == pair.Second.CharacterId
                         && pair.First.IsInSite == pair.Second.IsInSite
                         && pair.First.IsExternal == pair.Second.IsExternal
                         && pair.First.Reason == pair.Second.Reason
                         && pair.First.ReasonAmount == pair.Second.ReasonAmount
                         && pair.First.CharacterName == pair.Second.CharacterName);

    /// <summary>
    /// This decision, with the outcome <paramref name="standing"/> already carries when this one carries none — the one
    /// rule that keeps an outcome, once somebody set it, from ever being erased (ET-271, HF-7TQB/HF-ESNB): a list
    /// without an outcome is a list that did not speak about it — a window still on its first read, a fleet member on
    /// an older client, a copy back from a server older than the column — never a pick of "not decided", which nothing
    /// offers. Only another outcome replaces an outcome. Read off the run rather than off its list, so the Completed a
    /// new homefront run starts with (ET-274) stands before any list was written.
    /// </summary>
    public RunAttendanceDecision KeepingOutcomeOf(Entities.Run? standing) =>
        Outcome is null && CompletedWaveCount is null
        && standing is { } kept && (kept.HomefrontOutcome is not null || kept.HomefrontCompletedWaveCount is not null)
            ? this with
            {
                Outcome = kept.HomefrontOutcome,
                CompletedWaveCount = kept.HomefrontCompletedWaveCount,
                OutcomeFromGameLog = kept.HomefrontOutcomeFromGameLog
            }
            : this;

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
                run.AttendanceNotOnRosterCount ?? 0, source, setBy, setAt,
                run.HomefrontOutcome, run.HomefrontCompletedWaveCount, run.HomefrontOutcomeFromGameLog)
            : null;
}
