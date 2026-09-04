using EveUtils.Shared.Modules.Runs.Dtos;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>
/// One of the runs behind an activity — one per character who flew it. Named by character id where there is nothing
/// better: the client never fills a participant list, so a name here would have to be invented (ET-131 gap 5).
/// </summary>
/// <param name="nameOf">Turns a character id into a name where the caller has one — for a local character it does,
/// and the row above these already names the crew that way, so without it one row would name the same pilot two
/// different ways. Left out, every row falls back to the id, which is what the detail screen still does.</param>
public sealed class ActivityRunRowViewModel(ActivityRunDetailDto run, Func<long, string>? nameOf = null)
{
    public string CharacterText { get; } = nameOf?.Invoke(run.CharacterId) ?? $"character {run.CharacterId}";

    public string DurationText { get; } = run.StoppedAtUtc is { } stoppedAtUtc
        ? (stoppedAtUtc - run.StartedAtUtc).ToString(@"hh\:mm\:ss")
        : "still open";

    /// <summary>
    /// Where a typed duration is told apart from a measured one. The corrected moments are written over the start
    /// and stop themselves, so the figure beside this says nothing about its own origin — this line is the only
    /// thing that does (ET-98).
    /// </summary>
    public string TimeSourceText { get; } = run.TimesCorrectedAtUtc is { } correctedAtUtc
        ? $"corrected by hand at {correctedAtUtc.ToLocalTime():HH:mm}"
        : "measured";

    /// <summary>Both facts said out loud, because the interesting row is the one where they disagree — the hauler
    /// who flew the site and takes no share (ET-105).</summary>
    public string StandingText { get; } = (run.IsParticipant, run.IsPayoutEligible) switch
    {
        (true, true) => "flew it · takes a share",
        (true, false) => "flew it · no share",
        (false, true) => "did not fly it · takes a share",
        (false, false) => "did not fly it · no share"
    };
}
