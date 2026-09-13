using EveUtils.Shared.Modules.Gamelog.Models;

namespace EveUtils.Shared.Modules.Gamelog.Aggregation;

/// <summary>
/// Tracks combat damage for a single character: session totals plus a live DPS sampled against an externally
/// supplied "now". Sampling against wall-clock time (rather than the latest event) lets the value decay back to zero
/// when combat stops, which is what a live scrolling graph needs. Each weapon (outgoing) and each source (incoming) is
/// its own stream measured against its own cadence (<see cref="CadenceRate"/>), so a missile boat reads a flat DPS
/// between volleys instead of a sawtooth (ET-277). Folded from the EVE-Utils demo (own code). Lock-guarded so the
/// gamelog pump (Add), the fleet sampler and the UI render timer (Sample) can touch it concurrently.
/// </summary>
public sealed class LiveDpsTracker
{
    private readonly CadenceStreams _outgoing = new();
    private readonly CadenceStreams _incoming = new();
    private readonly Lock _gate = new();

    public long TotalDealt { get; private set; }
    public long TotalReceived { get; private set; }

    /// <param name="stream">What tells one cadence from another: the weapon for outgoing damage, the source (and its
    /// weapon, when the line names one) for incoming. Null folds the direction into one stream.</param>
    public void Add(DateTime at, DamageDirection direction, int amount, string? stream = null)
    {
        if (amount <= 0)
            return;

        lock (_gate)
        {
            if (direction == DamageDirection.Outgoing)
            {
                _outgoing.Add(stream ?? string.Empty, at, amount);
                TotalDealt += amount;
            }
            else
            {
                _incoming.Add(stream ?? string.Empty, at, amount);
                TotalReceived += amount;
            }
        }
    }

    public DpsSample Sample(DateTime now)
    {
        lock (_gate)
            return new DpsSample(_outgoing.Sample(now), _incoming.Sample(now));
    }
}
