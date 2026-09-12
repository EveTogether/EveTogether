namespace EveUtils.Shared.Modules.Gamelog.Aggregation;

/// <summary>
/// Attributes a residue line's units to the ore its character was last seen mining (ET-229) — the real residue line
/// ("Additional N units depleted from asteroid as residue") names no ore of its own. Measured against Jithran's own
/// gamelogs: the residue line always immediately follows, in the same second, the "You mined" line it belongs to,
/// with zero exceptions across the 2026-08-28/29 Metaliminal sites.
///
/// Kept per character, never globally: each client watches only its own gamelog, so one character's residue must
/// never attribute itself to another's last-mined ore.
/// </summary>
public sealed class MiningResidueCorrelator
{
    private readonly Dictionary<string, string> _lastOreByCharacter = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Remembers this character's ore, for the next residue line to attribute itself to.</summary>
    public void Observe(string character, string oreType) => _lastOreByCharacter[character] = oreType;

    /// <summary>The ore a residue line for this character belongs to, or null before any "You mined" line was ever
    /// seen for them (a watcher started mid-cycle) — dropped by the caller rather than guessed.</summary>
    public string? OreFor(string character) => _lastOreByCharacter.GetValueOrDefault(character);
}
