namespace EveUtils.Shared.Modules.Gamelog.Aggregation;

/// <summary>
/// A sliding-window rate of a single quantity (e.g. GJ neutralized or cap transmitted per second), sampled against an
/// externally supplied "now" so it decays back to zero when the activity stops — what a live scrolling graph line
/// needs. Direction-agnostic: callers that combine both directions (cap-warfare "activity") just add every amount.
/// Lock-guarded so the gamelog pump (<see cref="Add"/>) and the UI/fleet sampler (<see cref="Sample"/>) can race the
/// queue safely. The combat counterpart is <see cref="LiveDpsTracker"/>.
/// </summary>
public sealed class LiveRateTracker(TimeSpan? window = null)
{
    private readonly TimeSpan _window = window ?? TimeSpan.FromSeconds(5);
    private readonly Queue<Entry> _recent = new();
    private readonly Lock _gate = new();

    public void Add(DateTime at, int amount)
    {
        if (amount <= 0)
            return;
        lock (_gate)
            _recent.Enqueue(new Entry(at, amount));
    }

    /// <summary>The per-second rate over the trailing window at <paramref name="now"/> (0 once the window empties).</summary>
    public double Sample(DateTime now)
    {
        lock (_gate)
        {
            var cutoff = now - _window;
            while (_recent.Count > 0 && _recent.Peek().At < cutoff)
                _recent.Dequeue();

            long total = 0;
            foreach (var entry in _recent)
                total += entry.Amount;

            return total / _window.TotalSeconds;
        }
    }

    private readonly record struct Entry(DateTime At, int Amount);
}
