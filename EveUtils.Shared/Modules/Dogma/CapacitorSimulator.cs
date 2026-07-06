namespace EveUtils.Shared.Modules.Dogma;

/// <summary>
/// A discrete-event capacitor simulator (EVE's algorithm; our own implementation, validated against a reference oracle). It
/// steps an event queue of module activations, regenerating the capacitor between events with EVE's closed-form
/// recharge solution and applying each drain/fill as it fires. The run ends when the capacitor goes negative (unstable
/// — the time is the depletes-in time) or when the activation history repeats with no net loss (stable — the low/high
/// water marks give the equilibrium level). Identical modules are staggered (or, for turrets, fired together); cap
/// injectors top the capacitor back up from their charges.
/// </summary>
public sealed class CapacitorSimulator
{
    // Six hours: the stability horizon. A fit that has not run dry by then is treated as stable.
    private const double MaxTimeMs = 6 * 60 * 60 * 1000.0;
    private const int StabilityPrecision = 1;   // decimal digits of cap compared between periods

    private readonly double _capacity;
    private readonly double _tau;               // recharge time constant = rechargeRate / 5
    private readonly bool _stagger;
    private readonly bool _reload;
    private readonly bool _optimizeRepeats;

    /// <param name="capacitorCapacity">Total capacitor (GJ).</param>
    /// <param name="rechargeRateMs">The ship's capacitor recharge rate (ms).</param>
    /// <param name="stagger">Spread identical non-turret activations evenly (smooths the load), as EVE does.</param>
    /// <param name="reload">Account for charge reloads (cap boosters); off matches EVE's cap-stability readout.</param>
    /// <param name="optimizeRepeats">Reuse the computed result across identical repeating activation cycles instead of simulating each one (performance).</param>
    public CapacitorSimulator(double capacitorCapacity, double rechargeRateMs,
        bool stagger = true, bool reload = false, bool optimizeRepeats = true)
    {
        _capacity = capacitorCapacity;
        _tau = rechargeRateMs / 5.0;
        _stagger = stagger;
        _reload = reload;
        _optimizeRepeats = optimizeRepeats;
    }

    public CapacitorSimulation Run(IReadOnlyList<CapDrain> drains)
    {
        var (state, period) = BuildState(drains);
        if (state.Count == 0)
            return new CapacitorSimulation(Stable: true, StablePercent: 100, DepletesInSeconds: 0);

        var awaitingInjectors = new List<Event>();
        var awaitingInjectorsWrap = 0;

        double capWrap = _capacity, capLowest = _capacity, capLowestPre = _capacity, cap = _capacity;
        double tWrap = period, tLast = 0;

        while (state.Count > 0)
        {
            var activation = Pop(state);
            if (activation.Time >= MaxTimeMs)
                break;

            // Regenerate from the previous event using EVE's closed-form recharge solution.
            if (activation.Time > tLast)
                cap = Math.Pow(1.0 + (Math.Sqrt(cap / _capacity) - 1.0) * Math.Exp((tLast - activation.Time) / _tau), 2) * _capacity;

            if (activation.Time != tLast)
            {
                if (cap < capLowestPre)
                    capLowestPre = cap;
                if (Math.Abs(activation.Time - tWrap) < double.Epsilon)
                {
                    // History repeating: if we have at least as much cap as last period (and the same pending
                    // injectors), the setup is stable — stop early.
                    if (_optimizeRepeats && cap >= capWrap && awaitingInjectors.Count == awaitingInjectorsWrap)
                        break;
                    capWrap = Math.Round(cap, StabilityPrecision);
                    awaitingInjectorsWrap = awaitingInjectors.Count;
                    tWrap += period;
                }
            }

            tLast = activation.Time;

            // A fill that would overshoot the cap is postponed until it is needed.
            if (activation.IsInjector && cap - activation.CapNeed > _capacity)
            {
                awaitingInjectors.Add(activation);
                continue;
            }

            // Use pending injectors to cover this activation if we cannot afford it outright.
            if (activation.CapNeed > cap && cap < _capacity)
                DrainAwaitingInjectors(state, awaitingInjectors, ref cap, activation.Time, need: activation.CapNeed);

            cap -= activation.CapNeed;
            if (cap > _capacity)
                cap = _capacity;

            if (cap < capLowest)
            {
                if (cap < 0.0)
                    break;          // ran dry — unstable
                capLowest = cap;
            }

            // Top up from any pending injectors that fit without overshooting.
            DrainAwaitingInjectors(state, awaitingInjectors, ref cap, activation.Time, need: _capacity - cap, topUpOnly: true);

            Push(state, Requeue(activation));
        }

        var stable = cap > 0.0;
        if (!stable)
            return new CapacitorSimulation(Stable: false, StablePercent: 0, DepletesInSeconds: tLast / 1000.0);

        var statePercent = Math.Min(100.0, (capLowest + capLowestPre) / (2.0 * _capacity) * 100.0);
        return new CapacitorSimulation(Stable: true, StablePercent: statePercent, DepletesInSeconds: 0);
    }

    // Spend pending injectors to reach `need` GJ (or to top the cap up when topUpOnly), re-queuing each used injector.
    private void DrainAwaitingInjectors(List<Event> state, List<Event> awaiting, ref double cap, double time,
        double need, bool topUpOnly = false)
    {
        while (awaiting.Count > 0 && cap < _capacity && (topUpOnly || (need > cap && _capacity > cap)))
        {
            var current = cap;                       // snapshot: ref locals cannot be captured by the lambdas below
            var headroom = _capacity - current;
            // CapNeed is negative for a fill; -CapNeed is the GJ it provides.
            var candidates = topUpOnly
                ? awaiting.Where(injector => -injector.CapNeed <= headroom).ToList()
                : awaiting.Where(injector => -injector.CapNeed >= Math.Min(need - current, headroom)).ToList();

            Event chosen;
            if (candidates.Count > 0)
                chosen = candidates.MaxBy(injector => -injector.CapNeed)!;   // most cap among those that fit
            else if (topUpOnly)
                break;
            else
                chosen = awaiting.MaxBy(injector => -injector.CapNeed)!;     // nothing fits exactly: take the biggest

            awaiting.Remove(chosen);
            cap -= chosen.CapNeed;
            if (cap > _capacity)
                cap = _capacity;
            Push(state, Requeue(chosen with { Time = time }));
        }
    }

    private static Event Requeue(Event activation)
    {
        var time = activation.Time + activation.Duration;
        var shot = activation.Shot + 1;
        if (activation.ClipSize != 0 && shot % activation.ClipSize == 0)
        {
            shot = 0;
            time += activation.ReloadTime;
        }
        return activation with { Time = time, Shot = shot };
    }

    // Group identical modules, stagger them (turrets excepted) and seed the event queue; returns the optimisation period.
    private (List<Event> State, double Period) BuildState(IReadOnlyList<CapDrain> drains)
    {
        var groups = new Dictionary<CapDrain, int>();
        foreach (var drain in drains)
        {
            var keyed = _reload || drain.IsInjector ? drain : drain with { ClipSize = 0, ReloadTime = 0 };
            groups[keyed] = groups.GetValueOrDefault(keyed) + 1;
        }

        var state = new List<Event>();
        double period = 1;
        var disablePeriod = false;

        foreach (var (drain, amount) in groups)
        {
            if (drain.ClipSize != 0)
                disablePeriod = true;

            if (drain.IsInjector)
            {
                // Injectors are not staggered — they are used on demand and should be available immediately.
                for (var i = 0; i < amount; i++)
                    Push(state, ToEvent(drain, 0));
                continue;
            }

            var duration = drain.Duration;
            var capNeed = drain.CapNeed;
            if (_stagger && !drain.DisableStagger)
            {
                if (drain.ClipSize == 0)
                    duration = Math.Truncate(duration / amount);   // collapse identical mods into one faster event
                else
                {
                    var staggerStep = (drain.Duration * drain.ClipSize + drain.ReloadTime) / (amount * drain.ClipSize);
                    for (var i = 1; i < amount; i++)
                        Push(state, ToEvent(drain, i * staggerStep));
                }
            }
            else
                capNeed *= amount;     // fire together (turrets, or staggering off)

            period = Lcm(period, duration);
            Push(state, ToEvent(drain with { Duration = duration, CapNeed = capNeed }, 0));
        }

        return (state, disablePeriod ? MaxTimeMs : period);
    }

    private static Event ToEvent(CapDrain drain, double time) =>
        new(time, drain.Duration, drain.CapNeed, Shot: 0, drain.ClipSize, drain.ReloadTime, drain.IsInjector);

    private static double Lcm(double a, double b)
    {
        long la = (long)Math.Round(a), lb = (long)Math.Round(b);
        if (la == 0 || lb == 0)
            return Math.Max(la, lb);
        long gcd = Gcd(la, lb);
        return (double)(la / gcd * lb);
    }

    private static long Gcd(long a, long b)
    {
        while (b != 0)
            (a, b) = (b, a % b);
        return Math.Abs(a);
    }

    // A tiny binary min-heap ordered like the event lists: by time, then the remaining fields, so ties resolve
    // deterministically.
    private static void Push(List<Event> heap, Event item)
    {
        heap.Add(item);
        var child = heap.Count - 1;
        while (child > 0)
        {
            var parent = (child - 1) / 2;
            if (Compare(heap[child], heap[parent]) >= 0)
                break;
            (heap[child], heap[parent]) = (heap[parent], heap[child]);
            child = parent;
        }
    }

    private static Event Pop(List<Event> heap)
    {
        var root = heap[0];
        var last = heap.Count - 1;
        heap[0] = heap[last];
        heap.RemoveAt(last);
        var parent = 0;
        while (true)
        {
            var left = parent * 2 + 1;
            var right = left + 1;
            var smallest = parent;
            if (left < heap.Count && Compare(heap[left], heap[smallest]) < 0)
                smallest = left;
            if (right < heap.Count && Compare(heap[right], heap[smallest]) < 0)
                smallest = right;
            if (smallest == parent)
                break;
            (heap[parent], heap[smallest]) = (heap[smallest], heap[parent]);
            parent = smallest;
        }
        return root;
    }

    private static int Compare(Event a, Event b)
    {
        int c = a.Time.CompareTo(b.Time);
        if (c != 0) return c;
        c = a.Duration.CompareTo(b.Duration);
        if (c != 0) return c;
        c = a.CapNeed.CompareTo(b.CapNeed);
        if (c != 0) return c;
        c = a.Shot.CompareTo(b.Shot);
        if (c != 0) return c;
        c = a.ClipSize.CompareTo(b.ClipSize);
        if (c != 0) return c;
        c = a.ReloadTime.CompareTo(b.ReloadTime);
        if (c != 0) return c;
        return a.IsInjector.CompareTo(b.IsInjector);
    }

    private sealed record Event(
        double Time, double Duration, double CapNeed, int Shot, int ClipSize, double ReloadTime, bool IsInjector);
}
