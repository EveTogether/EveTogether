namespace EveUtils.Shared.Modules.Gamelog.Aggregation;

/// <summary>
/// The live per-second rate of a single quantity (e.g. GJ neutralized on you, or remote reps received), sampled
/// against an externally supplied "now" so it decays back to zero when the activity stops — what a live scrolling
/// graph line needs. Each <em>stream</em> (one module on one counterparty, as the log line names it) is measured
/// against its own cycle by <see cref="CadenceRate"/>, so a 14.6 s cap-transfer cycle reads as a steady 25 GJ/s
/// instead of blinking between 0 and 73 (ET-277). Lock-guarded so the gamelog pump (<see cref="Add"/>) and the
/// UI/fleet sampler (<see cref="Sample"/>) can race safely. The combat counterpart is <see cref="LiveDpsTracker"/>.
/// </summary>
public sealed class LiveRateTracker
{
    private readonly CadenceStreams _streams = new();
    private readonly Lock _gate = new();

    /// <param name="stream">What tells one cycle from another on this quantity — the counterparty and module the line
    /// names. Null folds everything into one stream, which still adapts, only less precisely.</param>
    public void Add(DateTime at, int amount, string? stream = null)
    {
        if (amount <= 0)
            return;
        lock (_gate)
            _streams.Add(stream ?? string.Empty, at, amount);
    }

    /// <summary>The per-second rate at <paramref name="now"/>: every stream's own rate, summed (0 once all have faded).</summary>
    public double Sample(DateTime now)
    {
        lock (_gate)
            return _streams.Sample(now);
    }
}
