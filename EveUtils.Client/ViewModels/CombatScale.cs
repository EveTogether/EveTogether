using System;
using EveUtils.Client.Controls;

namespace EveUtils.Client.ViewModels;

/// <summary>
/// The scale a set of combat meters and graphs share (ET-277): one for hp/s, one for GJ/s. The fleet screen hands one
/// instance to every member, so a pilot doing 90 dps no longer fills a bar as full as one doing 900 — every card used
/// to scale itself. It rises at once with the largest figure seen, holds there for <see cref="VisibleHold"/> — long
/// enough that the peak has scrolled behind the left edge of even the widest fleet card or pop-out graph — and only
/// then eases back down (half of the excess every 20 s), reading as a round ceiling so the bars do not creep.
///
/// This is the shared floor, not the last word: it does not know any one graph's actual pixel width, so
/// <see cref="Controls.DpsGraph"/> itself still widens further, per render, whenever its own on-screen history runs
/// ahead of it (ET-280) — a wide enough window can show more history than <see cref="VisibleHold"/> assumes. A meter
/// on its own gets an instance of its own.
/// </summary>
public sealed class CombatScale
{
    public const double HitPointsFloor = 100;
    public const double CapacitorFloor = 10;

    private static readonly TimeSpan HalfLife = TimeSpan.FromSeconds(20);

    // Long enough that a peak has scrolled off even the widest fleet-card or pop-out graph (18 px/s default; a 700px
    // graph shows ~39 s of history) before the scale is allowed to start easing back down. Decaying from the moment
    // the peak was seen — the old behaviour — could shrink the axis while the peak was still drawn on screen, which
    // clipped the line flat against the top edge (ET-280).
    private static readonly TimeSpan VisibleHold = TimeSpan.FromSeconds(40);

    private double _hitPointsPeak = HitPointsFloor;
    private DateTime _hitPointsPeakAt = DateTime.MinValue;
    private double _capacitorPeak = CapacitorFloor;
    private DateTime _capacitorPeakAt = DateTime.MinValue;
    private DateTime _now = DateTime.MinValue;

    /// <summary>The top of the hp/s scale, as of the last <see cref="Observe"/>.</summary>
    public double HitPoints => DpsGraph.NiceCeiling(Eased(_hitPointsPeak, _hitPointsPeakAt, HitPointsFloor, _now), HitPointsFloor);

    /// <summary>The top of the GJ/s scale, as of the last <see cref="Observe"/>.</summary>
    public double Capacitor => DpsGraph.NiceCeiling(Eased(_capacitorPeak, _capacitorPeakAt, CapacitorFloor, _now), CapacitorFloor);

    /// <summary>One member's current largest figures. Every member reports each frame; the scale keeps the largest
    /// still-held peak, across every member sharing this instance. <see cref="HitPoints"/>/<see cref="Capacitor"/>
    /// read as of this call's <paramref name="now"/> — not the wall clock — so the scale only ever moves when told to.</summary>
    public void Observe(double hitPoints, double capacitor, DateTime now)
    {
        _now = now;
        Track(ref _hitPointsPeak, ref _hitPointsPeakAt, hitPoints, HitPointsFloor, now);
        Track(ref _capacitorPeak, ref _capacitorPeakAt, capacitor, CapacitorFloor, now);
    }

    // A new observation that is at least the current (eased) value becomes the peak at once and restarts its hold —
    // that also covers a figure climbing back up part-way, below the old, already-decaying peak. Anything lower than
    // the current value leaves the held peak alone; it is read back through Eased, never decayed twice.
    private static void Track(ref double peak, ref DateTime peakAt, double observed, double floor, DateTime now)
    {
        if (observed < Eased(peak, peakAt, floor, now))
            return;
        peak = Math.Max(floor, observed);
        peakAt = now;
    }

    private static double Eased(double peak, DateTime peakAt, double floor, DateTime now)
    {
        if (peakAt == DateTime.MinValue)
            return Math.Max(floor, peak);

        var heldUntil = peakAt + VisibleHold;
        if (now <= heldUntil)
            return peak;

        var factor = Math.Pow(0.5, (now - heldUntil).TotalSeconds / HalfLife.TotalSeconds);
        return Math.Max(floor, peak * factor);
    }
}
