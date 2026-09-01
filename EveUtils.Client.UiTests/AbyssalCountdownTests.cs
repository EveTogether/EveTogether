using System;
using System.Globalization;
using EveUtils.Shared.Modules.Gamelog.Aggregation;
using EveUtils.Shared.Modules.Gamelog.Models;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// The run's 20:00 deadline is hard, so the countdown may never claim more time than the pilot has. Nothing is
/// written when a filament pulls you in, so the entry cannot be read — only bounded from below by the last moment
/// ESI proved the pilot was outside, which the "+" marks as a floor. ET-62 made ESI the only source of that moment.
/// </summary>
public class AbyssalCountdownTests
{
    private static readonly DateTime SeenOutside = new(2026, 8, 29, 17, 34, 34, DateTimeKind.Utc);
    private static readonly DateTime FirstContact = new(2026, 8, 29, 17, 35, 46, DateTimeKind.Utc);
    private static readonly DateTime At1740 = new(2026, 8, 29, 17, 40, 0, DateTimeKind.Utc);

    [Fact]
    public void Countdown_AnchorsOnTheLastSightingOutside_NotOnTheFirstShot()
    {
        var metrics = new CharacterMetrics();
        metrics.SeenOutside(SeenOutside);
        metrics.SeenInside(FirstContact);

        // The seconds between the last sighting and the entry are already spent; the clock has to have spent them too.
        Assert.Equal(SeenOutside, metrics.AbyssalAnchor);
        Assert.Equal("Abyssal (14:34+)", AbyssalSpace.Describe("Aphend", metrics.AbyssalAnchor, At1740));

        // Anchoring on the moment we first saw them INSIDE would have shown 15:46 — time the pilot does not have.
        Assert.Equal("Abyssal (15:46+)", AbyssalSpace.Describe("Aphend", FirstContact, At1740));
    }

    /// <summary>
    /// Asserts the DIRECTION, never a number of seconds: showing more time than the pilot has is what costs a ship.
    /// A seconds bound would be a false guarantee — the anchor comes from a poll, so the gap is at least the interval's
    /// own spread (measured 6.042-6.096 s) plus truncation, and any fixed bound goes red on a slow day.
    /// </summary>
    [Theory]
    [InlineData(0)]       // sighting and entry coincide
    [InlineData(1)]
    [InlineData(6_048)]   // measured gap between the last outside poll and the first inside one
    [InlineData(6_096)]   // measured worst interval
    [InlineData(30_000)]  // a slow day: the guarantee must not depend on the interval
    [InlineData(600_000)]
    public void TheReadout_NeverShowsMoreTimeThanThePilotHas(int lagMs)
    {
        var entry = new DateTime(2026, 8, 29, 22, 17, 39, DateTimeKind.Utc);  // measured entry of the ET-62 test run
        var metrics = new CharacterMetrics();

        metrics.SeenOutside(entry - TimeSpan.FromMilliseconds(lagMs));
        metrics.SeenInside(entry);

        // The anchor is exactly the sighting — no drift of its own beyond the lag it was handed.
        Assert.Equal(TimeSpan.FromMilliseconds(lagMs), entry - metrics.AbyssalAnchor!.Value);

        for (var minutesIn = 0; minutesIn <= 15; minutesIn += 5)
        {
            var now = entry + TimeSpan.FromMinutes(minutesIn);
            var truth = AbyssalSpace.RunLimit - (now - entry);
            var shown = Shown(AbyssalSpace.Describe("Aphend", metrics.AbyssalAnchor, now));

            Assert.True(shown <= truth, $"lag {lagMs} ms, {minutesIn} min in: showed {shown}, pilot has {truth}");
        }
    }

    /// <summary>Reads a countdown back out of the readout. "--:--" and a bare system name both claim no time at all.</summary>
    private static TimeSpan Shown(string? readout)
    {
        if (readout is not { } text || !text.StartsWith("Abyssal (", StringComparison.Ordinal))
            return TimeSpan.Zero;

        var body = text["Abyssal (".Length..].TrimEnd(')').TrimEnd('+');
        return body == "--:--"
            ? TimeSpan.Zero
            : TimeSpan.ParseExact(body, "mm\\:ss", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The regression this ticket exists for. A gamelog location line may be any age — the watcher replays the last
    /// one at start-up — so it may never anchor a clock. Before ET-62 this produced "--:--" for a whole live run.
    /// </summary>
    [Fact]
    public void AGamelogLocationLine_NamesTheSystemButNeverAnchorsTheClock()
    {
        var staleUndock = new DateTime(2026, 8, 29, 20, 54, 17, DateTimeKind.Utc);
        var entry = new DateTime(2026, 8, 29, 21, 40, 18, DateTimeKind.Utc);  // 46:01 later
        var metrics = new CharacterMetrics();

        metrics.SetLocation("Aphend", staleUndock);
        metrics.SeenInside(entry);

        Assert.Equal("Aphend", metrics.Location);
        // No sighting outside, so there is no run to time — and emphatically not one anchored 46 minutes early.
        Assert.Null(metrics.AbyssalAnchor);
        Assert.NotEqual("Abyssal (--:--)", AbyssalSpace.Describe("Aphend", metrics.AbyssalAnchor, entry));
    }

    [Fact]
    public void PastTheDeadline_TheClockSaysUnknown_RatherThanCountingOn()
    {
        var expired = SeenOutside + AbyssalSpace.RunLimit + TimeSpan.FromSeconds(1);
        Assert.Equal("Abyssal (--:--)", AbyssalSpace.Describe("Aphend", SeenOutside, expired));
    }

    /// <summary>
    /// Combat no longer says anything about where the pilot is. The old name list could not see a filament whose NPCs
    /// were all absent from it, and mistook normal-space Triglavians for the abyss; ESI's id range has neither problem.
    /// </summary>
    [Fact]
    public void Combat_NoLongerOpensOrClosesARun()
    {
        var metrics = new CharacterMetrics();
        metrics.SeenOutside(SeenOutside);

        metrics.RecordCombat(DamageDirection.Outgoing, 120, "Striking Damavik", HitQuality.Hits);
        metrics.RecordCombat(DamageDirection.Incoming, 90, "Lucid Watchman", HitQuality.Hits);

        Assert.Null(metrics.AbyssalAnchor);
        Assert.Equal(120, metrics.Snapshot("Pilot").TotalDealt);
    }

    /// <summary>
    /// A fleet mate's countdown crosses the wire as an anchor stamped on THEIR clock. Read against ours it would be
    /// off by whatever the two machines disagree by — and a receiver running behind would show that difference as
    /// extra time, which is the one direction that costs a ship. Only the span between their anchor and their sample
    /// is taken from them; both halves of the sum are then measured on the receiver's own clock.
    /// </summary>
    [Fact]
    public void ASlowReceiverClock_DoesNotHandOutExtraTime()
    {
        var senderAnchor = new DateTimeOffset(SeenOutside).ToUnixTimeMilliseconds();
        var senderSentAt = new DateTimeOffset(At1740).ToUnixTimeMilliseconds();  // 5:26 into the run
        var truth = "Abyssal (14:34+)";

        // Receiver in step: same answer as the sender's own row.
        var inStep = AbyssalSpace.AnchorFromWire(senderAnchor, senderSentAt, At1740);
        Assert.Equal(truth, AbyssalSpace.Describe("Aphend", inStep, At1740));

        // Receiver 90 seconds behind. Reading the raw anchor against its own clock would have said 16:04.
        var slowNow = At1740 - TimeSpan.FromSeconds(90);
        var rebased = AbyssalSpace.AnchorFromWire(senderAnchor, senderSentAt, slowNow);
        Assert.Equal(truth, AbyssalSpace.Describe("Aphend", rebased, slowNow));
        Assert.Equal("Abyssal (16:04+)", AbyssalSpace.Describe("Aphend", SeenOutside, slowNow));

        // Receiver 90 seconds ahead — same answer, so the correction is not a one-sided fudge.
        var fastNow = At1740 + TimeSpan.FromSeconds(90);
        Assert.Equal(truth, AbyssalSpace.Describe("Aphend", AbyssalSpace.AnchorFromWire(senderAnchor, senderSentAt, fastNow), fastNow));
    }

    [Fact]
    public void NetworkDelay_ShowsLessTimeNotMore()
    {
        var senderAnchor = new DateTimeOffset(SeenOutside).ToUnixTimeMilliseconds();
        var senderSentAt = new DateTimeOffset(At1740).ToUnixTimeMilliseconds();

        // The sample arrives 10 seconds late; those 10 seconds are spent, and the readout has to have spent them.
        var arrived = At1740 + TimeSpan.FromSeconds(10);
        var anchor = AbyssalSpace.AnchorFromWire(senderAnchor, senderSentAt, arrived);
        Assert.Equal("Abyssal (14:34+)", AbyssalSpace.Describe("Aphend", anchor, arrived));
        Assert.Equal("Abyssal (14:24+)", AbyssalSpace.Describe("Aphend", anchor, arrived + TimeSpan.FromSeconds(10)));
    }

    /// <summary>
    /// Two runs without docking — Raymond confirms this is the normal way to fly them, not the edge case. Nothing is
    /// written when the second filament goes off, so the sighting that ended run one is what run two anchors on. With
    /// continuous polling that sighting is seconds old; before ET-62 it was as old as the gap between the runs.
    /// </summary>
    [Fact]
    public void ASecondRunWithoutDocking_AnchorsOnTheLatestSighting()
    {
        var runOneExit = new DateTime(2026, 8, 29, 21, 48, 4, DateTimeKind.Utc);   // measured
        var betweenRuns = new DateTime(2026, 8, 29, 21, 53, 58, DateTimeKind.Utc); // still outside, polled
        var runTwoEntry = new DateTime(2026, 8, 29, 21, 54, 4, DateTimeKind.Utc);

        var metrics = new CharacterMetrics();
        metrics.SeenOutside(runOneExit);
        Assert.Null(metrics.AbyssalAnchor);

        // Six minutes of flying around in normal space, every poll re-proving they are outside.
        metrics.SeenOutside(betweenRuns);
        metrics.SeenInside(runTwoEntry);

        // The anchor is the LAST sighting, not the exit six minutes earlier — that difference was the bug.
        Assert.Equal(betweenRuns, metrics.AbyssalAnchor);
        Assert.Equal("Abyssal (19:54+)", AbyssalSpace.Describe("Aphend", metrics.AbyssalAnchor, runTwoEntry));
        Assert.Equal("Abyssal (14:00+)", AbyssalSpace.Describe("Aphend", runOneExit, runTwoEntry));
    }

    /// <summary>Losing the watch clears the countdown rather than letting it run on against a frozen anchor.</summary>
    [Fact]
    public void LosingTheWatch_ClearsTheClock()
    {
        var metrics = new CharacterMetrics();
        metrics.SeenOutside(SeenOutside);
        metrics.SeenInside(FirstContact);
        Assert.NotNull(metrics.AbyssalAnchor);

        metrics.AbyssalWatchLost(null);
        Assert.Null(metrics.AbyssalAnchor);
    }

    [Fact]
    public void BeingSeenOutside_EndsTheRun()
    {
        var metrics = new CharacterMetrics();
        metrics.SeenOutside(SeenOutside);
        metrics.SeenInside(FirstContact);
        Assert.NotNull(metrics.AbyssalAnchor);

        metrics.SeenOutside(At1740);
        Assert.Null(metrics.AbyssalAnchor);

        // ...and that same sighting is what the next run will anchor on.
        metrics.SeenInside(At1740 + TimeSpan.FromSeconds(6));
        Assert.Equal(At1740, metrics.AbyssalAnchor);
    }

    /// <summary>
    /// The two guards <see cref="AbyssalSpace.Remaining"/> carries, asserted on it directly rather than only through
    /// the sentence <see cref="AbyssalSpace.Describe"/> wraps around it — the activity window reads the figure
    /// without that sentence (ET-98), so both callers have to be held to the same clamp.
    /// </summary>
    [Fact]
    public void Remaining_NeverExceedsTheRunLimit_AndIsGoneOnceTheDeadlinePasses()
    {
        Assert.Null(AbyssalSpace.Remaining(null, At1740));

        // An anchor stamped ahead of us (log time against wall clock) must not buy the pilot extra seconds.
        Assert.Equal(AbyssalSpace.RunLimit, AbyssalSpace.Remaining(At1740.AddMinutes(3), At1740));

        Assert.Equal(TimeSpan.FromMinutes(14), AbyssalSpace.Remaining(At1740.AddMinutes(-6), At1740));

        // Past zero the readout stops counting rather than running on into negative time.
        Assert.Null(AbyssalSpace.Remaining(At1740 - AbyssalSpace.RunLimit, At1740));
        Assert.Equal("Abyssal (--:--)", AbyssalSpace.Describe("Aphend", At1740 - AbyssalSpace.RunLimit, At1740));
    }

    /// <summary>Staying inside must not re-anchor: the clock would stand still at full time forever.</summary>
    [Fact]
    public void RepeatedInsideReadings_DoNotMoveTheAnchor()
    {
        var metrics = new CharacterMetrics();
        metrics.SeenOutside(SeenOutside);
        metrics.SeenInside(FirstContact);

        metrics.SeenInside(At1740);
        metrics.SeenInside(At1740 + TimeSpan.FromMinutes(5));

        Assert.Equal(SeenOutside, metrics.AbyssalAnchor);
    }
}
