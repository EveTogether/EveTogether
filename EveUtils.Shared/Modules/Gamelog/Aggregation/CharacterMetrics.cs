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
    long NeutIn)
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
    private double _peakDealtDps;

    public void RecordCombat(DamageDirection direction, int amount, string target, HitQuality quality)
    {
        lock (_gate)
        {
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

    public void SetLocation(string system)
    {
        lock (_gate)
            _location = system;
    }

    /// <summary>The character's last known solar system (gamelog jump/undock), or null until one is seen.</summary>
    public string? Location
    {
        get { lock (_gate) return _location; }
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
                _mining.Totals(), _mining.TotalUnits, _repairedOut, _repairedIn, _neutOut, _neutIn);
        }
    }
}
