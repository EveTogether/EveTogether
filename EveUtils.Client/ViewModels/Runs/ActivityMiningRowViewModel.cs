using EveUtils.Client.Formatting;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>One character's own mining of one ore (ET-229) — general for any mining, not only a homefront's. No
/// timestamps: the activity already runs from a start to a stop time (Jithran, 2026-09-11).</summary>
/// <param name="nameOf">Turns a character id into a name where the caller has one — see
/// <see cref="ActivityRunRowViewModel"/> for why this is not always possible yet.</param>
public sealed class ActivityMiningRowViewModel(
    Guid runId, long characterId, string oreType, int units, int criticalUnits, int residueUnits, decimal? value,
    bool isFixedPrice, Func<long, string>? nameOf = null)
{
    /// <summary>The run this ore was mined on — read back by the run window to sum one participant's own share
    /// (<see cref="Sections.MiningWindowSectionViewModel.FactsFor"/>), the same way CONSUMABLES' row carries it.</summary>
    public Guid RunId { get; } = runId;

    /// <summary>The raw figure <see cref="ValueText"/> is built from, for a caller that sums rather than displays.</summary>
    public decimal? Value { get; } = value;

    /// <summary>The raw units <see cref="UnitsText"/> is built from (crit included), for a caller that sums rather
    /// than displays — the fleet's own "fleet mined" line (ET-234).</summary>
    public int Units { get; } = units;

    /// <summary>The raw residue <see cref="ResidueText"/> is built from, for the same reason as <see cref="Units"/> —
    /// the fleet's own "remaining" line, when the site's capacity is known (ET-234).</summary>
    public int ResidueUnits { get; } = residueUnits;

    public string CharacterText { get; } = nameOf?.Invoke(characterId) ?? $"character {characterId}";

    public string OreText { get; } = oreType;

    public string UnitsText { get; } = criticalUnits > 0
        ? $"{IskFormat.Number(units)} units ({IskFormat.Number(criticalUnits)} crit)"
        : $"{IskFormat.Number(units)} units";

    public string ResidueText { get; } = residueUnits > 0 ? $"{IskFormat.Number(residueUnits)} residue" : "no residue";

    /// <summary>"no price yet" until it can be valued at all; the NPC-buy exception is named rather than folded
    /// silently into the figure (ET-229).</summary>
    public string ValueText { get; } = value is { } isk
        ? IskFormat.Whole(isk) + (isFixedPrice ? " (NPC price)" : string.Empty)
        : "no price yet";
}
