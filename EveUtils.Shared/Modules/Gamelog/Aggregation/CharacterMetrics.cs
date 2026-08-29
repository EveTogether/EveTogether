using System.Threading;
using EveUtils.Shared.Modules.Gamelog.Models;

namespace EveUtils.Shared.Modules.Gamelog.Aggregation;

/// <summary>One enemy type and how often it was engaged (outgoing combat lines).</summary>
public sealed record EnemyCount(string Target, int Count);

/// <summary>A notable gamelog event (notify/warning) surfaced for the metrics view.</summary>
public sealed record NotableEvent(DateTime At, string Message);

/// <summary>Immutable point-in-time view of a character's session metrics, for the UI.</summary>
public sealed record CharacterMetricsSnapshot(
    string Character,
    long TotalDealt,
    long TotalReceived,
    int Hits,
    int Misses,
    IReadOnlyDictionary<HitQuality, int> Qualities,
    IReadOnlyList<EnemyCount> Enemies,
    long BountyTotal,
    int Kills,
    string? Location,
    double PeakDealtDps,
    TimeSpan Duration,
    IReadOnlyList<NotableEvent> RecentEvents,
    IReadOnlyList<OreTotal> Mined,
    long TotalMinedUnits,
    long RepairedOut,
    long RepairedIn,
    long NeutOut,
    long NeutIn,
    DateTime? AbyssalAnchor)
{
    public int Shots => Hits + Misses;
    public double HitRate => Shots == 0 ? 0 : (double)Hits / Shots;
    public double IskPerHour => Duration.TotalHours <= 0 ? 0 : BountyTotal / Duration.TotalHours;
}

/// <summary>
/// Accumulates one character's session metrics from the gamelog stream: combat totals + hit/miss +
/// quality breakdown + engaged enemies, bounty/kills, current location, peak DPS and recent notable events.
/// All mutations and <see cref="Snapshot"/> are guarded by one lock — the watcher pump writes while the UI
/// reads on its own timer. Cumulative since app start (in-memory; not persisted in the POC).
/// </summary>
public sealed class CharacterMetrics
{
    private const int MaxRecentEvents = 25;

    private readonly Lock _gate = new();
    private readonly Dictionary<HitQuality, int> _qualities = new();
    private readonly Dictionary<string, int> _enemies = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<NotableEvent> _recent = new();

    private readonly MiningLedgerAggregator _mining = new();
    private readonly DateTime _sessionStart = DateTime.UtcNow;
    private long _dealt, _received, _bounty, _repairedOut, _repairedIn, _neutOut, _neutIn;
    private int _hits, _misses, _kills;
    private string? _location;
    // The last moment we could PROVE the character was outside the abyss — a jump/undock line, or the ESI poll that
    // saw them leave. Only such a moment may anchor a countdown, because only it is certainly before the entry.
    private DateTime? _lastKnownOutsideAt;
    private DateTime? _abyssalAnchor;
    private double _peakDealtDps;

    public void RecordCombat(DamageDirection direction, int amount, string target, HitQuality quality)
    {
        lock (_gate)
        {
            // First abyssal name of this run: the pilot has been inside since the last place we could see them.
            if (_abyssalAnchor is null && _lastKnownOutsideAt is { } anchor && AbyssalSpace.IsAbyssalContact(target))
                _abyssalAnchor = anchor;

            var miss = quality == HitQuality.Misses || amount <= 0;
            if (miss)
            {
                _misses++;
            }
            else
            {
                _hits++;
                _qualities[quality] = _qualities.GetValueOrDefault(quality) + 1;
                if (direction == DamageDirection.Outgoing) _dealt += amount;
                else _received += amount;
            }

            // Count every outgoing engagement (hit or miss) as an enemy encountered.
            if (direction == DamageDirection.Outgoing && !string.IsNullOrWhiteSpace(target))
                _enemies[target] = _enemies.GetValueOrDefault(target) + 1;
        }
    }

    public void RecordBounty(long isk)
    {
        lock (_gate)
        {
            _bounty += isk;
            _kills++;
        }
    }

    public void RecordMining(MiningEvent mining)
    {
        lock (_gate)
            _mining.Add(mining);
    }

    /// <summary>A remote rep: <paramref name="outgoing"/> = you repped someone, else you were repped.</summary>
    public void RecordRemoteRep(bool outgoing, int amount)
    {
        if (amount <= 0)
            return;
        lock (_gate)
        {
            if (outgoing) _repairedOut += amount;
            else _repairedIn += amount;
        }
    }

    /// <summary>An energy-neutralizer hit: <paramref name="outgoing"/> = you neuted a target,
    /// else cap was neutralized on you. Cumulative GJ per direction; a 0 GJ tick (out of range) adds nothing.</summary>
    public void RecordNeut(bool outgoing, int amount)
    {
        if (amount <= 0)
            return;
        lock (_gate)
        {
            if (outgoing) _neutOut += amount;
            else _neutIn += amount;
        }
    }

    /// <summary>Seed persisted cumulative figures on load: bounty/kills + mined units per ore.</summary>
    public void SeedPersisted(long bountyTotal, int kills, IReadOnlyList<OreTotal> mined)
    {
        lock (_gate)
        {
            _bounty += bountyTotal;
            _kills += kills;
            foreach (var ore in mined)
                _mining.SeedUnits(ore.OreType, ore.Units);
        }
    }

    /// <summary>
    /// A jump or undock: the character is somewhere new, so any abyssal run is over. This is the certain exit, but
    /// not the usual one — you leave the abyss where you fired the filament, and no line is written there.
    /// <see cref="EndAbyssalRun"/> is what closes an ordinary run.
    /// </summary>
    public void SetLocation(string system, DateTime at)
    {
        lock (_gate)
        {
            _location = system;
            _lastKnownOutsideAt = at;
            _abyssalAnchor = null;
        }
    }

    /// <summary>
    /// Ends the countdown because ESI placed the character outside the abyss (or, with <paramref name="seenOutsideUtc"/>
    /// null, because the deadline passed and we stopped watching).
    ///
    /// Recording the sighting matters as much as clearing the clock: a second filament is fired in space, so a
    /// follow-up run has no location line to anchor on and would otherwise fall back on an undock from three runs
    /// ago and read "--:--" from arrival (measured 2026-08-29: one undock at 19:19:50 covered three runs).
    /// </summary>
    public void EndAbyssalRun(DateTime? seenOutsideUtc)
    {
        lock (_gate)
        {
            _abyssalAnchor = null;
            if (seenOutsideUtc is { } outside)
                _lastKnownOutsideAt = outside;
        }
    }

    /// <summary>The character's last known solar system (gamelog jump/undock), or null until one is seen.</summary>
    public string? Location
    {
        get { lock (_gate) return _location; }
    }

    /// <summary>
    /// When the abyssal countdown started, or null when no run is under way. This is the last moment we could prove
    /// the character was outside, never the first abyssal shot: the shot is minutes into a run that already started,
    /// and anchoring there would show more time left than the pilot has.
    /// </summary>
    public DateTime? AbyssalAnchor
    {
        get { lock (_gate) return _abyssalAnchor; }
    }

    public void RecordNotify(DateTime at, string message)
    {
        lock (_gate)
        {
            _recent.Enqueue(new NotableEvent(at, message));
            while (_recent.Count > MaxRecentEvents)
                _recent.Dequeue();
        }
    }

    public void ObservePeakDps(double dealtPerSecond)
    {
        lock (_gate)
            if (dealtPerSecond > _peakDealtDps)
                _peakDealtDps = dealtPerSecond;
    }

    public CharacterMetricsSnapshot Snapshot(string character)
    {
        lock (_gate)
        {
            var enemies = _enemies
                .OrderByDescending(kv => kv.Value)
                .Select(kv => new EnemyCount(kv.Key, kv.Value))
                .ToList();
            // Most-recent first for display.
            var recent = _recent.Reverse().ToList();
            return new CharacterMetricsSnapshot(
                character, _dealt, _received, _hits, _misses,
                new Dictionary<HitQuality, int>(_qualities), enemies,
                _bounty, _kills, _location, _peakDealtDps,
                DateTime.UtcNow - _sessionStart, recent,
                _mining.Totals(), _mining.TotalUnits, _repairedOut, _repairedIn, _neutOut, _neutIn, _abyssalAnchor);
        }
    }
}
