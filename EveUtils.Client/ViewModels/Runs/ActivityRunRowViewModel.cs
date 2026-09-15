using Material.Icons;
using EveUtils.Client.Formatting;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Isk;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>
/// One of the runs behind an activity — one per character who flew it, a line of its own under the unfolded row
/// (ET-290): their face, their own ISK, their own time and where their run stands towards a server. Named from what was
/// recorded on the run itself when it started (ET-212); a run saved before that column existed, or synced from a
/// fleetmate's older client, has none, and falls back to a live lookup and then to the bare id.
/// </summary>
/// <param name="nameOf">Turns a character id into a name where the caller has one and <see cref="ActivityRunDetailDto.CharacterNameSnapshot"/>
/// is empty — for a local character still logged in it does, and the row above these already names the crew that
/// way, so without it one row would name the same pilot two different ways. Left out, every such row falls back to
/// the id, which is what the detail screen still does.</param>
/// <param name="face">The screen's shared face for this pilot; an initial of their own when null.</param>
/// <param name="isk">This character's own share of the activity's TOTAL ISK (ET-272), by the same registry the
/// total was added up with.</param>
/// <param name="activity">The row this run belongs to — a click on the run opens the same detail it does.</param>
public sealed class ActivityRunRowViewModel(ActivityRunDetailDto run, Func<long, string>? nameOf = null,
    CharacterFaceViewModel? face = null, IskBreakdown? isk = null, ActivityOverviewRowViewModel? activity = null)
{
    public string CharacterText { get; } = CharacterNameResolver.Resolve(run.CharacterNameSnapshot, run.CharacterId, nameOf);

    public CharacterFaceViewModel Face { get; } = face
        ?? new CharacterFaceViewModel(run.CharacterId, CharacterNameResolver.Resolve(run.CharacterNameSnapshot, run.CharacterId, nameOf));

    public ActivityOverviewRowViewModel? Activity { get; } = activity;

    public string DurationText { get; } = run.StoppedAtUtc is { } stoppedAtUtc
        ? (stoppedAtUtc - run.StartedAtUtc).ToString(@"hh\:mm\:ss")
        : "still open";

    /// <summary>A dash rather than "0 ISK" when nothing of theirs was valued — a zero reads as a valuation that came
    /// out at nothing.</summary>
    public string IskText { get; } = isk is { HasFigure: true } own
        ? (own.Total < 0 ? string.Empty : "+") + IskFormat.Compact(own.Total) + " ISK"
        : "—";

    /// <summary>
    /// Where a typed duration is told apart from a measured one. The corrected moments are written over the start
    /// and stop themselves, so the figure beside this says nothing about its own origin — this line is the only
    /// thing that does (ET-98).
    /// </summary>
    public string TimeSourceText { get; } = run.TimesCorrectedAtUtc is { } correctedAtUtc
        ? $"corrected by hand at {correctedAtUtc.ToLocalTime():HH:mm}"
        : "measured";

    /// <summary>Only what is out of the ordinary about this character's run (ET-272): out of the homefront's site, did
    /// not fly it, or left out of the loot split — the hauler's row (ET-105). A run in the site with its share, the
    /// normal case, says nothing.</summary>
    public string StandingText { get; } = string.Join(" · ", new[]
    {
        run.InSiteAtCompletion is false ? "not in site" : run.InSiteAtCompletion is null && !run.IsParticipant ? "did not fly it" : null,
        run.IsPayoutEligible ? null : "no loot share"
    }.OfType<string>());

    public bool HasSyncIcon { get; } = run.SyncState is not RunSyncState.Local;

    public MaterialIconKind SyncIcon { get; } = run.SyncState switch
    {
        RunSyncState.Pending => MaterialIconKind.ClockOutline,
        RunSyncState.Outdated => MaterialIconKind.ArrowUpBoldCircleOutline,
        _ => MaterialIconKind.CloudCheckOutline
    };

    public string SyncText { get; } = run.SyncState switch
    {
        RunSyncState.Pending => "queued for a server",
        RunSyncState.Outdated => "changed since published",
        RunSyncState.Synced => "published",
        _ => "only on this machine"
    };
}
