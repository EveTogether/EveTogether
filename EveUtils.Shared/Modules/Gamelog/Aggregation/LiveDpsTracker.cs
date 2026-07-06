using EveUtils.Shared.Modules.Gamelog.Models;

namespace EveUtils.Shared.Modules.Gamelog.Aggregation;

/// <summary>
/// Tracks combat damage for a single character: session totals plus a sliding-window DPS sampled
/// against an externally supplied "now". Sampling against wall-clock time (rather than the latest
/// event) lets the value decay back to zero when combat stops, which is what a live scrolling graph
/// needs. Folded from the EVE-Utils demo (own code). Lock-guarded so the gamelog pump (Add), the fleet
/// sampler and the UI render timer (Sample) can touch it concurrently without racing the queue.
/// </summary>
public sealed class LiveDpsTracker(TimeSpan? window = null)
{
    private readonly TimeSpan _window = window ?? TimeSpan.FromSeconds(5);
    private readonly Queue<Hit> _recent = new();
    private readonly Lock _gate = new();

    public long TotalDealt { get; private set; }
    public long TotalReceived { get; private set; }

    public void Add(DateTime at, DamageDirection direction, int amount)
    {
        if (amount <= 0)
            return;

        lock (_gate)
        {
            _recent.Enqueue(new Hit(at, amount, direction));

            if (direction == DamageDirection.Outgoing)
                TotalDealt += amount;
            else
                TotalReceived += amount;
        }
    }

    public DpsSample Sample(DateTime now)
    {
        lock (_gate)
        {
            var cutoff = now - _window;
            while (_recent.Count > 0 && _recent.Peek().At < cutoff)
                _recent.Dequeue();

            long dealt = 0, received = 0;
            foreach (var hit in _recent)
            {
                if (hit.Direction == DamageDirection.Outgoing)
                    dealt += hit.Amount;
                else
                    received += hit.Amount;
            }

            var seconds = _window.TotalSeconds;
            return new DpsSample(dealt / seconds, received / seconds);
        }
    }

    private readonly record struct Hit(DateTime At, int Amount, DamageDirection Direction);
}
