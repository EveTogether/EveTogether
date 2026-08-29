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
/// Timestamps are Raymond's measured run of 2026-08-29 (ET-55): undock 17:34:34, first abyssal name the short
/// vocabulary catches 17:39:07. A later detection only moves when the clock appears, never what it says — the
/// anchor is the undock either way.
/// </summary>
public class AbyssalCountdownTests
{
    private static readonly DateTime Undock = new(2026, 8, 29, 17, 34, 34, DateTimeKind.Utc);
    private static readonly DateTime FirstContact = new(2026, 8, 29, 17, 39, 7, DateTimeKind.Utc);
    private static readonly DateTime At1740 = new(2026, 8, 29, 17, 40, 0, DateTimeKind.Utc);

    [Fact]
    public void Countdown_AnchorsOnTheUndock_NotOnTheFirstShot()
    {
        var metrics = new CharacterMetrics();
        metrics.SetLocation("Aphend", Undock);
        metrics.RecordCombat(DamageDirection.Outgoing, 120, "Triglavian Biocombinative Cache", HitQuality.Hits);

        // The minutes between undock and first contact are already gone; the clock has to have spent them.
        Assert.Equal(Undock, metrics.AbyssalAnchor);
        Assert.Equal("Abyssal (14:34)", AbyssalSpace.Describe("Aphend", metrics.AbyssalAnchor, At1740));

        // Anchoring on the contact instead would have shown 19:07 — four and a half minutes the pilot does not have.
        Assert.Equal("Abyssal (19:07)", AbyssalSpace.Describe("Aphend", FirstContact, At1740));
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

        Assert.Null(metrics.AbyssalAnchor);
        Assert.Equal("Aphend", AbyssalSpace.Describe("Aphend", metrics.AbyssalAnchor, At1740));
    }

    [Fact]
    public void LeavingOnAJump_EndsTheRun()
    {
        var metrics = new CharacterMetrics();
        metrics.SetLocation("Aphend", Undock);
        metrics.RecordCombat(DamageDirection.Outgoing, 120, "Triglavian Biocombinative Cache", HitQuality.Hits);
        Assert.NotNull(metrics.AbyssalAnchor);

        metrics.SetLocation("Kamela", At1740);
        Assert.Null(metrics.AbyssalAnchor);
    }
}
