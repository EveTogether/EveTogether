namespace EveUtils.Shared.Modules.Gamelog.Aggregation;

/// <summary>
/// The per-second rate of ONE stream of game-log amounts — one weapon's hits, one neutralizer's cycles — measured
/// against the stream's own cadence instead of a fixed window. A fixed 5 s window reads a 10 s missile volley as a
/// spike and then nothing (ET-277); here every amount is spread evenly over the interval the stream is observed to
/// repeat at (a "hold"), and the result is averaged over at least two of those intervals. Sustained fire therefore
/// reads flat at total ÷ time, a stream that stops holds for one interval and then fades to zero over the averaging
/// window, and a stream as fast as the old window keeps the old window's responsiveness.
///
/// Not thread-safe: the owning tracker serialises access.
/// </summary>
internal sealed class CadenceRate
{
    /// <summary>The shortest averaging window — the fixed window every rate used before ET-277.</summary>
    internal static readonly TimeSpan BaseWindow = TimeSpan.FromSeconds(5);

    // A game-log time has one-second resolution, so a volley whose lines straddle a second boundary is still one
    // volley. Lines up to this long after a volley's first line belong to it.
    private const double VolleySpreadSeconds = 1;

    // A longer silence is a pause between engagements (a warp, a new target pack), not a weapon cycle. The slowest
    // cycles in the game — large artillery, capital cap transfers — stay well under it.
    private const double MaxIntervalSeconds = 30;

    // Below this a hold would be shorter than the log's own timestamp jitter.
    private const double MinHoldSeconds = 2;

    private const int IntervalMemory = 8;

    private readonly Queue<Entry> _entries = new();
    private readonly Queue<double> _gaps = new();
    private DateTime _volleyStart;
    private DateTime _burstStart;
    private DateTime _lastAt;

    /// <summary>When the stream last had an amount, in log time; <see cref="DateTime.MinValue"/> before the first.</summary>
    public DateTime LastAt => _lastAt;

    /// <summary>How long one amount is spread out: the observed interval between volleys, or the base window while
    /// the stream has not repeated yet.</summary>
    internal TimeSpan Hold => TimeSpan.FromSeconds(Math.Max(_Interval() ?? BaseWindow.TotalSeconds, MinHoldSeconds));

    /// <summary>What the held amounts are averaged over: at least two intervals, never less than the base window.</summary>
    internal TimeSpan Window => TimeSpan.FromSeconds(Math.Max(BaseWindow.TotalSeconds, 2 * Hold.TotalSeconds));

    public void Add(DateTime at, int amount)
    {
        if (amount <= 0)
            return;

        if (_entries.Count == 0 || at - _lastAt > Hold + Window)
        {
            // The stream had faded to zero, so this is a new engagement: it is read from its own start rather than
            // averaged against the silence before it, which would make every opening volley ramp in over the window.
            _burstStart = at;
            _volleyStart = at;
        }
        else if ((at - _volleyStart).TotalSeconds > VolleySpreadSeconds)
        {
            var gap = (at - _volleyStart).TotalSeconds;
            if (gap <= MaxIntervalSeconds)
            {
                _gaps.Enqueue(gap);
                if (_gaps.Count > IntervalMemory)
                    _gaps.Dequeue();
            }
            _volleyStart = at;
        }

        if (at > _lastAt)
            _lastAt = at;
        _entries.Enqueue(new Entry(at, amount));
    }

    public double Sample(DateTime now)
    {
        if (_entries.Count == 0)
            return 0;

        var hold = Hold.TotalSeconds;
        var window = Window.TotalSeconds;

        // The game's clock may run a little ahead of this machine's; a line stamped a second into our future is not
        // hidden until our clock catches up with it.
        var end = now > _lastAt ? now : _lastAt;

        // Pruned against the longest reach any hold can have, not the current one: the interval estimate can still
        // grow, and a line dropped under a shorter hold would then be missing from the longer one.
        while (_entries.Count > 0 && (end - _entries.Peek().At).TotalSeconds > 3 * MaxIntervalSeconds)
            _entries.Dequeue();
        if (_entries.Count == 0)
            return 0;

        var span = Math.Min(window, (end - _burstStart).TotalSeconds);
        if (span <= 0)
            return _HeldAt(end, hold);

        var start = end.AddSeconds(-span);
        double integral = 0;
        foreach (var entry in _entries)
        {
            var holdEnd = entry.At.AddSeconds(hold);
            var overlapStart = entry.At > start ? entry.At : start;
            var overlapEnd = holdEnd < end ? holdEnd : end;
            var overlap = (overlapEnd - overlapStart).TotalSeconds;
            if (overlap > 0)
                integral += entry.Amount / hold * overlap;
        }

        return integral / span;
    }

    private double _HeldAt(DateTime at, double hold)
    {
        double rate = 0;
        foreach (var entry in _entries)
            if (entry.At <= at && (at - entry.At).TotalSeconds < hold)
                rate += entry.Amount / hold;
        return rate;
    }

    // The mean gap is the stream's true period even when the log's whole-second stamps make the single gaps alternate
    // (a 2.5 s cycle logs as 2, 3, 2, 3), and a window of twice that period averages the alternation out exactly. The
    // longest gap is left out when it is plainly not a cycle — over twice the median, a reload or a retarget pause —
    // so one such pause does not stretch the hold for the next eight volleys.
    private double? _Interval()
    {
        if (_gaps.Count == 0)
            return null;

        var sorted = _gaps.Order().ToList();
        var median = sorted[sorted.Count / 2];
        return sorted.Count >= 4 && sorted[^1] > 2 * median
            ? sorted.Take(sorted.Count - 1).Average()
            : sorted.Average();
    }

    private readonly record struct Entry(DateTime At, int Amount);
}
