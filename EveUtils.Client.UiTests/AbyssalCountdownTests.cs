using System;
using EveUtils.Shared.Modules.Gamelog.Aggregation;
using EveUtils.Shared.Modules.Gamelog.Models;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-56: the abyssal run has a hard 20:00 deadline, after which ship and pod are destroyed. The countdown may
/// therefore never claim more time than the pilot has. The log gives no entry event — the first thing we see is a
/// shot fired, well into the run — so the clock anchors on the last place the log put the pilot, not on that shot.
///
/// That anchor sits before the real entry, so the number is a lower bound and the readout says so with a trailing
/// "+" — the blind window was 72 s, 84 s and 3.5 minutes on three measured runs, which is too much to call exact.
///
/// Timestamps are Raymond's measured run of 2026-08-29 (ET-55): undock 17:34:34, first contact 17:35:46.
/// </summary>
public class AbyssalCountdownTests
{
    private static readonly DateTime Undock = new(2026, 8, 29, 17, 34, 34, DateTimeKind.Utc);
    private static readonly DateTime FirstContact = new(2026, 8, 29, 17, 35, 46, DateTimeKind.Utc);
    private static readonly DateTime At1740 = new(2026, 8, 29, 17, 40, 0, DateTimeKind.Utc);

    [Fact]
    public void Countdown_AnchorsOnTheUndock_NotOnTheFirstShot()
    {
        var metrics = new CharacterMetrics();
        metrics.SetLocation("Aphend", Undock);
        metrics.RecordCombat(DamageDirection.Outgoing, 120, "Striking Damavik", HitQuality.Hits);

        // The 72 seconds between undock and first shot are already spent; the clock has to have spent them too.
        Assert.Equal(Undock, metrics.AbyssalAnchor);
        Assert.Equal("Abyssal (14:34+)", AbyssalSpace.Describe("Aphend", metrics.AbyssalAnchor, At1740));

        // Anchoring on the shot instead would have shown 15:46 — 72 seconds the pilot does not have.
        Assert.Equal("Abyssal (15:46+)", AbyssalSpace.Describe("Aphend", FirstContact, At1740));
    }

    [Fact]
    public void PastTheDeadline_TheClockSaysUnknown_RatherThanCountingOn()
    {
        var expired = Undock + AbyssalSpace.RunLimit + TimeSpan.FromSeconds(1);
        Assert.Equal("Abyssal (--:--)", AbyssalSpace.Describe("Aphend", Undock, expired));
    }

    [Fact]
    public void NormalSpaceCombat_LeavesTheLocationAlone()
    {
        var metrics = new CharacterMetrics();
        metrics.SetLocation("Aphend", Undock);
        metrics.RecordCombat(DamageDirection.Outgoing, 120, "Serpentis Scout", HitQuality.Hits);
        // A bare Triglavian hull flies in normal space; only an adjective in front of it means the abyss.
        metrics.RecordCombat(DamageDirection.Outgoing, 120, "Damavik", HitQuality.Hits);

        Assert.Null(metrics.AbyssalAnchor);
        Assert.Equal("Aphend", AbyssalSpace.Describe("Aphend", metrics.AbyssalAnchor, At1740));
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
        var senderAnchor = new DateTimeOffset(Undock).ToUnixTimeMilliseconds();
        var senderSentAt = new DateTimeOffset(At1740).ToUnixTimeMilliseconds();  // 5:26 into the run
        var truth = "Abyssal (14:34+)";

        // Receiver in step: same answer as the sender's own row.
        var inStep = AbyssalSpace.AnchorFromWire(senderAnchor, senderSentAt, At1740);
        Assert.Equal(truth, AbyssalSpace.Describe("Aphend", inStep, At1740));

        // Receiver 90 seconds behind. Reading the raw anchor against its own clock would have said 16:04.
        var slowNow = At1740 - TimeSpan.FromSeconds(90);
        var rebased = AbyssalSpace.AnchorFromWire(senderAnchor, senderSentAt, slowNow);
        Assert.Equal(truth, AbyssalSpace.Describe("Aphend", rebased, slowNow));
        Assert.Equal("Abyssal (16:04+)", AbyssalSpace.Describe("Aphend", Undock, slowNow));

        // Receiver 90 seconds ahead — same answer, so the correction is not a one-sided fudge.
        var fastNow = At1740 + TimeSpan.FromSeconds(90);
        Assert.Equal(truth, AbyssalSpace.Describe("Aphend", AbyssalSpace.AnchorFromWire(senderAnchor, senderSentAt, fastNow), fastNow));
    }

    [Fact]
    public void NetworkDelay_ShowsLessTimeNotMore()
    {
        var senderAnchor = new DateTimeOffset(Undock).ToUnixTimeMilliseconds();
        var senderSentAt = new DateTimeOffset(At1740).ToUnixTimeMilliseconds();

        // The sample arrives 10 seconds late; those 10 seconds are spent, and the readout has to have spent them.
        var arrived = At1740 + TimeSpan.FromSeconds(10);
        var anchor = AbyssalSpace.AnchorFromWire(senderAnchor, senderSentAt, arrived);
        Assert.Equal("Abyssal (14:34+)", AbyssalSpace.Describe("Aphend", anchor, arrived));
        Assert.Equal("Abyssal (14:24+)", AbyssalSpace.Describe("Aphend", anchor, arrived + TimeSpan.FromSeconds(10)));
    }

    /// <summary>
    /// Two runs on one undock. You fire the second filament in space, so no location line is written between them —
    /// measured 2026-08-29: a single undock at 19:19:50 covered three runs. Anchoring the second on that undock puts
    /// it thirteen minutes past its start and it reads "--:--" from arrival, so the sighting that ended run one has
    /// to become the anchor for run two.
    /// </summary>
    [Fact]
    public void ASecondRunAnchorsOnTheSightingThatEndedTheFirst()
    {
        var undock = new DateTime(2026, 8, 29, 19, 19, 50, DateTimeKind.Utc);
        var seenOutside = new DateTime(2026, 8, 29, 19, 30, 12, DateTimeKind.Utc);
        var runTwoFirstShot = new DateTime(2026, 8, 29, 19, 32, 52, DateTimeKind.Utc);

        var metrics = new CharacterMetrics();
        metrics.SetLocation("Aphend", undock);
        metrics.RecordCombat(DamageDirection.Outgoing, 120, "Starving Damavik", HitQuality.Hits);
        Assert.Equal(undock, metrics.AbyssalAnchor);

        // ESI put them back in normal space; that is the last thing we can prove about where they were.
        metrics.EndAbyssalRun(seenOutside);
        Assert.Null(metrics.AbyssalAnchor);

        metrics.RecordCombat(DamageDirection.Outgoing, 120, "Starving Damavik", HitQuality.Hits);
        Assert.Equal(seenOutside, metrics.AbyssalAnchor);

        // Live rather than already expired, and still a floor: the anchor is before the real entry.
        Assert.Equal("Abyssal (17:20+)", AbyssalSpace.Describe("Aphend", metrics.AbyssalAnchor, runTwoFirstShot));
    }

    /// <summary>Giving up without a sighting clears the countdown but must not invent a new anchor.</summary>
    [Fact]
    public void EndingWithoutASighting_ClearsTheClockButProvesNothing()
    {
        var metrics = new CharacterMetrics();
        metrics.SetLocation("Aphend", Undock);
        metrics.RecordCombat(DamageDirection.Outgoing, 120, "Striking Damavik", HitQuality.Hits);

        metrics.EndAbyssalRun(null);
        Assert.Null(metrics.AbyssalAnchor);

        metrics.RecordCombat(DamageDirection.Outgoing, 120, "Striking Damavik", HitQuality.Hits);
        Assert.Equal(Undock, metrics.AbyssalAnchor);
    }

    [Fact]
    public void LeavingOnAJump_EndsTheRun()
    {
        var metrics = new CharacterMetrics();
        metrics.SetLocation("Aphend", Undock);
        metrics.RecordCombat(DamageDirection.Outgoing, 120, "Striking Damavik", HitQuality.Hits);
        Assert.NotNull(metrics.AbyssalAnchor);

        metrics.SetLocation("Kamela", At1740);
        Assert.Null(metrics.AbyssalAnchor);
    }
}
