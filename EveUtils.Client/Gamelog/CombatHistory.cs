using System;
using System.Collections.Concurrent;
using EveUtils.Client.Controls;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Fleet.Metrics;

namespace EveUtils.Client.Gamelog;

/// <summary>
/// The rolling combat graph history of each own character: one ring of drawn samples per metric kind, written by the
/// character's <c>DpsViewModel</c> every render frame and read both by that graph and by the Local API, so a widget
/// that connects mid-fight gets the same recent past the DPS pop-out shows.
/// </summary>
public sealed class CombatHistory : ISingletonService
{
    /// <summary>Samples kept per line: about five minutes at the nominal <see cref="FramesPerSecond"/>.</summary>
    public const int Capacity = 9000;

    /// <summary>The render rate the rings are written at (see <c>DpsRenderDriver</c>), which is what turns a span of
    /// seconds into a number of samples.</summary>
    public const int FramesPerSecond = 30;

    private readonly ConcurrentDictionary<(string Character, MetricKind Kind), SampleRing> _rings = new();

    internal SampleRing RingFor(string character, MetricKind kind) =>
        _rings.GetOrAdd((character.ToUpperInvariant(), kind), _ => new SampleRing(Capacity));

    /// <summary>One value per second for the last <paramref name="seconds"/>, oldest→newest, newest being "now". Fewer
    /// when the character has not been drawn that long; empty for one with no history.</summary>
    public double[] PerSecond(string character, MetricKind kind, int seconds) =>
        _rings.TryGetValue((character.ToUpperInvariant(), kind), out var ring)
            ? ring.Strided(FramesPerSecond, seconds)
            : [];
}
