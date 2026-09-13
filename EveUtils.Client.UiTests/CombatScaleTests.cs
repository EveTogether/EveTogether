using System;
using EveUtils.Client.ViewModels;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-280: the shared <see cref="CombatScale"/> must never read lower than a peak that could still be on screen. The
/// old scale decayed toward the current rate on a wall-clock half-life starting the instant a higher rate was seen,
/// so a graph wide enough to still show that peak drew it flat against the top edge. The scale now holds a peak for
/// a fixed window before it eases down at all.
/// </summary>
public sealed class CombatScaleTests
{
    private static readonly DateTime Start = new(2030, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Observe_RisesAtOnce_ToTheLargestFigureSeen()
    {
        var scale = new CombatScale();
        scale.Observe(hitPoints: 340, capacitor: 0, Start);

        Assert.Equal(500, scale.HitPoints); // nice ceiling above 340
    }

    [Fact]
    public void Observe_HoldsThePeak_WhileLowerFiguresKeepArriving_WellWithinTheHoldWindow()
    {
        var scale = new CombatScale();
        scale.Observe(500, 0, Start);

        // 30 s later — comfortably short of the hold, long enough that the old wall-clock half-life (20 s) would
        // already have cut a peak of 500 roughly in half.
        for (var t = 1; t <= 30; t++)
            scale.Observe(20, 0, Start.AddSeconds(t));

        Assert.Equal(500, scale.HitPoints);
    }

    [Fact]
    public void Observe_EasesDown_OnlyAfterThePeakHasHadTimeToScrollOffScreen()
    {
        var scale = new CombatScale();
        scale.Observe(500, 0, Start);

        scale.Observe(20, 0, Start.AddSeconds(39)); // just short of the 40 s hold
        var stillHeld = scale.HitPoints;

        scale.Observe(20, 0, Start.AddSeconds(120)); // well past it
        var wellPastHold = scale.HitPoints;

        Assert.Equal(500, stillHeld);
        Assert.True(wellPastHold < 500, $"expected the scale to have eased down by 120 s, was {wellPastHold}");
        Assert.True(wellPastHold >= CombatScale.HitPointsFloor);
    }

    [Fact]
    public void Observe_ANewHigherFigure_ExtendsTheHold()
    {
        var scale = new CombatScale();
        scale.Observe(500, 0, Start);
        scale.Observe(20, 0, Start.AddSeconds(50)); // past the first hold, scale starts easing
        scale.Observe(800, 0, Start.AddSeconds(51)); // a fresh, higher peak

        Assert.Equal(1000, scale.HitPoints); // nice ceiling above 800

        scale.Observe(20, 0, Start.AddSeconds(80)); // 29 s after the new peak — still well within ITS hold
        Assert.Equal(1000, scale.HitPoints);
    }

    [Fact]
    public void Observe_NeverGoesBelowTheFloor()
    {
        var scale = new CombatScale();
        scale.Observe(0, 0, Start);
        scale.Observe(0, 0, Start.AddSeconds(200));

        Assert.Equal(CombatScale.HitPointsFloor, scale.HitPoints);
        Assert.Equal(CombatScale.CapacitorFloor, scale.Capacitor);
    }
}
