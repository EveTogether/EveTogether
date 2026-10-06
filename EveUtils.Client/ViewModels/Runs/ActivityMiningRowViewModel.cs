using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Formatting;
using EveUtils.Shared.Modules.Runs.Isk;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>One character's own mining of one ore (ET-229) — general for any mining, not only a homefront's. No
/// timestamps: the activity already runs from a start to a stop time (Jithran, 2026-09-11).
///
/// <paramref name="shareFraction"/> is this ore line's own inline bar (ET-283, variant C of the mining-ledger
/// mockups): this character's share, 0–1, of this ore within the counted fleet — or, solo, this ore's own share of
/// this character's ISK across every ore they mined. Null draws no bar at all, for a shared member whose client has
/// not sent per-ore lines (ET-234's total-only fallback), where there is no fleet-wide ore total to compare against.</summary>
/// <param name="nameOf">Turns a character id into a name where the caller has one — see
/// <see cref="ActivityRunRowViewModel"/> for why this is not always possible yet.</param>
/// <param name="corrector">What the line's edit action (ET-424) writes through. Null for a line that is not this
/// machine's to correct — someone else's, or one that stands for several runs at once — which then has no edit action.</param>
/// <param name="isLivePrice">Valued at today's cache price because the line has no price of its own fixed yet (ET-463).</param>
public sealed partial class ActivityMiningRowViewModel(
    Guid runId, long characterId, string oreType, int units, int criticalUnits, int residueUnits, decimal? value,
    bool isFixedPrice, Func<long, string>? nameOf = null, double? shareFraction = null, string? shareTooltip = null,
    MiningLineCorrector? corrector = null, bool isLivePrice = false) : ObservableObject
{
    /// <summary>What an ISK figure holding Mutanite says on hover (ET-288) — the fact ET-229 used to append to the
    /// figure itself as " (NPC price)", which no ISK column in the run window has room for.</summary>
    public static string FixedPriceTooltip { get; } =
        $"Mutanite is valued at its fixed NPC buy price, {IskFormat.Whole(MiningValuation.MutaniteNpcBuyPricePerUnit)} " +
        "per unit, not at the market's.";

    /// <summary>The run this ore was mined on — read back by the run window to sum one participant's own share
    /// (<see cref="Sections.MiningWindowSectionViewModel.FactsFor"/>), the same way CONSUMABLES' row carries it.</summary>
    public Guid RunId { get; } = runId;

    /// <summary>The raw figure <see cref="ValueText"/> is built from, for a caller that sums rather than displays.</summary>
    public decimal? Value { get; } = value;

    /// <summary>The raw units <see cref="UnitsText"/> is built from (crit included), for a caller that sums rather
    /// than displays — the fleet's own "fleet mined" line (ET-234).</summary>
    public int Units { get; } = units;

    /// <summary>The raw crit units already inside <see cref="Units"/>, for a caller that sums rather than displays.</summary>
    public int CriticalUnits { get; } = criticalUnits;

    /// <summary>The raw residue <see cref="ResidueText"/> is built from, for the same reason as <see cref="Units"/> —
    /// the fleet's own "remaining" line, when the site's capacity is known (ET-234).</summary>
    public int ResidueUnits { get; } = residueUnits;

    public bool IsFixedPrice { get; } = isFixedPrice;

    public string CharacterText { get; } = nameOf?.Invoke(characterId) ?? $"character {characterId}";

    public string OreText { get; } = oreType;

    /// <summary>The grouped figure alone, crit included — the crit part has its own column (<see cref="CritText"/>,
    /// ET-288): "61,554 (+1,200 crit)" in one cell pushed the figure out of its column in a 6-character homefront.</summary>
    public string UnitsText { get; } = IskFormat.Number(units);

    /// <summary>How much of <see cref="UnitsText"/> came from crits, "+1,200"; "—" for none (ET-288).</summary>
    public string CritText { get; } = criticalUnits > 0 ? "+" + IskFormat.Number(criticalUnits) : "—";

    /// <summary>Bare figure only (ET-284) — the RESIDUE column header above it already says what it is; "—" for
    /// none, matching the mockup's own dash rather than restating "no residue" in every row.</summary>
    public string ResidueText { get; } = residueUnits > 0 ? IskFormat.Number(residueUnits) : "—";

    /// <summary>Bare amount (ET-288) — the ISK header above it names the unit, the same as UNITS and RESIDUE; "—"
    /// until it can be valued at all, with <see cref="ValueTooltip"/> saying why.</summary>
    public string ValueText { get; } = value is { } isk ? IskFormat.Number(isk) + (isLivePrice ? " · live" : string.Empty) : "—";

    /// <summary>The NPC-buy exception, named rather than folded silently into the figure (ET-229) — or that there is
    /// no price yet, or only today's.</summary>
    public string? ValueTooltip { get; } = isFixedPrice ? FixedPriceTooltip
        : value is null ? "No price for this ore yet."
        : isLivePrice ? "Valued at today's price until this ore's price is fixed." : null;

    public bool HasShareBar { get; } = shareFraction is not null;

    public double ShareFraction { get; } = shareFraction ?? 0;

    public string ShareText { get; } = shareFraction is { } fraction
        ? (fraction >= 0.9995 ? "100" : fraction > 0 && fraction < 0.005 ? "<1" : Math.Round(fraction * 100)) + "%"
        : string.Empty;

    public string? ShareTooltip { get; } = shareTooltip;

    /// <summary>Zebra striping on the activity detail screen (ET-285) — every second ore row, set by the caller
    /// once the row's final position in its character group is known. Unused (stays false) on the run window,
    /// which does not stripe its own copy of this table.</summary>
    public bool IsAlternate { get; set; }

    private readonly MiningLineCorrector? _corrector = corrector;

    /// <summary>Whether the line has an edit action at all — see <paramref name="corrector"/>.</summary>
    public bool CanEdit => _corrector is not null && RunId != Guid.Empty;

    /// <summary>The box under the line is open: the units to set, or the whole line to take off the run.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotEditing))]
    private bool _isEditing;

    public bool IsNotEditing => !IsEditing;

    [ObservableProperty] private string _editText = string.Empty;

    /// <summary>Why the last correction was not taken, beside the box it was typed in.</summary>
    [ObservableProperty] private string? _editError;

    /// <summary>A correction is being written; the box waits rather than take a second click on top of the first.</summary>
    [ObservableProperty] private bool _isBusy;

    [RelayCommand]
    private void BeginEdit()
    {
        EditText = Units.ToString(CultureInfo.InvariantCulture);
        EditError = null;
        IsEditing = true;
    }

    [RelayCommand]
    private void CancelEdit() => IsEditing = false;

    [RelayCommand]
    private async Task SaveEditAsync()
    {
        if (_corrector is null || IsBusy)
            return;

        if (!int.TryParse(EditText.Trim(), NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out int newUnits) || newUnits < 1)
        {
            EditError = "Enter a whole number of units, at least 1.";
            return;
        }

        if (newUnits == Units)
        {
            IsEditing = false;
            return;
        }

        await _StoreAsync(() => _corrector.SetUnitsAsync(RunId, OreText, newUnits));
    }

    [RelayCommand]
    private async Task RemoveLineAsync()
    {
        if (_corrector is null || IsBusy)
            return;

        await _StoreAsync(() => _corrector.RemoveAsync(RunId, OreText));
    }

    private async Task _StoreAsync(Func<Task<string?>> store)
    {
        IsBusy = true;
        try
        {
            EditError = await store();
            IsEditing = EditError is not null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>The same line of the same run, whatever its figures now say — what lets the run window keep the row a
    /// pilot has open for editing while the mining behind it moves on.</summary>
    public bool IsSameLine(ActivityMiningRowViewModel other) => RunId == other.RunId && OreText == other.OreText;

    /// <summary>Whether <paramref name="other"/> draws exactly this row — what lets the run window keep the row it
    /// already shows rather than rebuild its container every clock tick (ET-287).</summary>
    public bool ShowsSameAs(ActivityMiningRowViewModel other) =>
        RunId == other.RunId && CharacterText == other.CharacterText && OreText == other.OreText
        && Units == other.Units && CriticalUnits == other.CriticalUnits && ResidueUnits == other.ResidueUnits
        && Value == other.Value && IsFixedPrice == other.IsFixedPrice && HasShareBar == other.HasShareBar
        && ShareFraction.Equals(other.ShareFraction) && ShareTooltip == other.ShareTooltip && IsAlternate == other.IsAlternate;
}
