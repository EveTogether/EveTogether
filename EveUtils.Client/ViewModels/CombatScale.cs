using System;
using EveUtils.Client.Controls;

namespace EveUtils.Client.ViewModels;

/// <summary>
/// The scale a set of combat meters and graphs share (ET-277): one for hp/s, one for GJ/s. The fleet screen hands one
/// instance to every member, so a pilot doing 90 dps no longer fills a bar as full as one doing 900 — every card used
/// to scale itself. It rises at once with the largest figure seen and sinks back slowly (half of the excess every
/// 20 s), and reads as a round ceiling so the bars do not creep. A meter on its own gets an instance of its own.
/// </summary>
public sealed class CombatScale
{
    public const double HitPointsFloor = 100;
    public const double CapacitorFloor = 10;

    private static readonly TimeSpan HalfLife = TimeSpan.FromSeconds(20);

    private double _hitPoints = HitPointsFloor;
    private double _capacitor = CapacitorFloor;
    private DateTime _decayedAt = DateTime.MinValue;

    /// <summary>The top of the hp/s scale.</summary>
    public double HitPoints => DpsGraph.NiceCeiling(_hitPoints, HitPointsFloor);

    /// <summary>The top of the GJ/s scale.</summary>
    public double Capacitor => DpsGraph.NiceCeiling(_capacitor, CapacitorFloor);

    /// <summary>One member's current largest figures. Every member reports each frame; the scale keeps the largest.</summary>
    public void Observe(double hitPoints, double capacitor, DateTime now)
    {
        if (_decayedAt != DateTime.MinValue && now > _decayedAt)
        {
            var factor = Math.Pow(0.5, (now - _decayedAt).TotalSeconds / HalfLife.TotalSeconds);
            _hitPoints = Math.Max(HitPointsFloor, _hitPoints * factor);
            _capacitor = Math.Max(CapacitorFloor, _capacitor * factor);
        }
        _decayedAt = now;

        _hitPoints = Math.Max(_hitPoints, hitPoints);
        _capacitor = Math.Max(_capacitor, capacitor);
    }
}
