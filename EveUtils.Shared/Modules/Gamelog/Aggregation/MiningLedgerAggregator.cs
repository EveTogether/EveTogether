using EveUtils.Shared.Modules.Gamelog.Models;

namespace EveUtils.Shared.Modules.Gamelog.Aggregation;

/// <summary>Accumulates mined units per ore type for a single character. Folded from the EVE-Utils demo.</summary>
public sealed class MiningLedgerAggregator
{
    private readonly Dictionary<string, Entry> _byOre = new(StringComparer.OrdinalIgnoreCase);

    public long TotalUnits { get; private set; }
    public long TotalCritical { get; private set; }
    public int OreTypeCount => _byOre.Count;

    public void Add(MiningEvent mining)
    {
        if (!_byOre.TryGetValue(mining.OreType, out var entry))
        {
            entry = new Entry();
            _byOre[mining.OreType] = entry;
        }

        entry.Units += mining.Units;
        entry.LostResidue += mining.LostResidue;
        if (mining.IsCritical)
            entry.CriticalUnits += mining.Units;

        TotalUnits += mining.Units;
        if (mining.IsCritical)
            TotalCritical += mining.Units;
    }

    /// <summary>Seed previously-persisted units for an ore; no critical/residue detail kept.</summary>
    public void SeedUnits(string oreType, long units)
    {
        if (units <= 0)
            return;
        if (!_byOre.TryGetValue(oreType, out var entry))
        {
            entry = new Entry();
            _byOre[oreType] = entry;
        }
        entry.Units += units;
        TotalUnits += units;
    }

    public IReadOnlyList<OreTotal> Totals() =>
        _byOre
            .Select(kv => new OreTotal(kv.Key, kv.Value.Units, kv.Value.CriticalUnits, kv.Value.LostResidue))
            .OrderByDescending(o => o.Units)
            .ToList();

    private sealed class Entry
    {
        public long Units;
        public long CriticalUnits;
        public long LostResidue;
    }
}
