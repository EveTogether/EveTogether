namespace EveUtils.Client.ViewModels.Runs;

/// <summary>One character's mining, grouped under a header row with its own ore lines indented beneath it (ET-283,
/// variant C of the mining-ledger mockups — the smallest step from BOUNTY's flat rows). The header carries the
/// character's own total, ISK/h and residue; each <see cref="Ores"/> row underneath carries the inline share bar,
/// its own units (with crit), residue and ISK.
///
/// Local characters and fleet mates that share their mining, per ore, look the same. Three exceptions:
/// <see cref="IsFallbackTotalOnly"/> (a shared member on a client that has not sent per-ore lines yet, ET-234's old
/// shape — an "all ores" line, no bar, no ISK), <see cref="IsNotShared"/> (an external member with no mining shared
/// at all, ET-272's convention — a name-only row, never counted), and neither, the normal per-ore case.</summary>
public sealed class MiningCharacterGroupViewModel(
    long characterId, string characterName, bool isLocal, decimal? isk, string rateText, string? rateTooltip,
    string residueText, string? residueTooltip, string? boostGlyph, string? boostTooltip,
    IReadOnlyList<ActivityMiningRowViewModel> ores, string? fallbackText = null, bool isNotShared = false)
{
    public long CharacterId { get; } = characterId;

    public string CharacterName { get; } = characterName;

    public bool IsLocal { get; } = isLocal;

    public string IskText { get; } = Formatting.IskFormat.WholeOrNoPrice(isk);

    /// <summary>The run window's "now" (last 5 minutes) or the detail screen's "mining time avg · whole run avg" —
    /// each caller builds its own text, this row only ever shows it.</summary>
    public string RateText { get; } = rateText;

    public string? RateTooltip { get; } = rateTooltip;

    public string ResidueText { get; } = residueText;

    /// <summary>The residue's own ISK value, shown only on hover (ET-283) — residue never counts toward
    /// <see cref="IskText"/>, it left the rock but never reached the hold.</summary>
    public string? ResidueTooltip { get; } = residueTooltip;

    /// <summary>▲▲ boosting (this character's own gamelog wrote the burst lines) or ▲ boosted (inferred from a local
    /// booster's log — never from a receiver's own log, which says nothing) — null where nothing is determinable,
    /// never "not boosted" (ET-283).</summary>
    public string? BoostGlyph { get; } = boostGlyph;

    public string? BoostTooltip { get; } = boostTooltip;

    public IReadOnlyList<ActivityMiningRowViewModel> Ores { get; } = ores;

    public bool HasOres { get; } = ores.Count > 0;

    /// <summary>Set only for a shared member whose client has not sent per-ore lines yet (ET-234's old shape) —
    /// "all ores · N units", no bar, no ISK. Null once the per-ore wire is there, and for this window's own rows.</summary>
    public string? FallbackText { get; } = fallbackText;

    public bool IsFallbackTotalOnly { get; } = fallbackText is not null;

    /// <summary>An external fleet member with no mining shared at all (ET-272's convention) — a name-only row, never
    /// counted in the total.</summary>
    public bool IsNotShared { get; } = isNotShared;
}
