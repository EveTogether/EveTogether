using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>One character's mining, grouped under a header row with its own ore lines indented beneath it (ET-283,
/// variant C of the mining-ledger mockups — the smallest step from BOUNTY's flat rows). The header carries the
/// character's own total, ISK/h and residue; each <see cref="Ores"/> row underneath carries the inline share bar,
/// its own units (with crit), residue and ISK.
///
/// Local characters and fleet mates that share their mining, per ore, look the same. Three exceptions:
/// <see cref="IsFallbackTotalOnly"/> (a shared member on a client that has not sent per-ore lines yet, ET-234's old
/// shape — an "all ores" line, no bar, no ISK), <see cref="IsNotShared"/> (an external member with no mining shared
/// at all, ET-272's convention — a name-only row, never counted), and neither, the normal per-ore case.
///
/// Observable (ET-287): the run window keeps one of these per character for the whole run and moves only what changed
/// onto it (<see cref="TakeOver"/>). Rebuilding every group each clock tick recreated every row's container, share bar
/// and tooltip once a second.</summary>
public sealed partial class MiningCharacterGroupViewModel : ObservableObject
{
    public MiningCharacterGroupViewModel(
        long characterId, string characterName, bool isLocal, decimal? isk, string rateText, string? rateTooltip,
        string residueText, string? residueTooltip, string? boostGlyph, string? boostTooltip,
        IReadOnlyList<ActivityMiningRowViewModel> ores, string? fallbackText = null, bool isNotShared = false)
    {
        CharacterId = characterId;
        IsLocal = isLocal;
        IsNotShared = isNotShared;
        _characterName = characterName;
        _iskText = Formatting.IskFormat.WholeOrNoPrice(isk);
        _rateText = rateText;
        _rateTooltip = rateTooltip;
        _residueText = residueText;
        _residueTooltip = residueTooltip;
        _boostGlyph = boostGlyph;
        _boostTooltip = boostTooltip;
        _fallbackText = fallbackText;
        Ores = [.. ores];
    }

    public long CharacterId { get; }

    [ObservableProperty] private string _characterName;

    public bool IsLocal { get; }

    [ObservableProperty] private string _iskText;

    /// <summary>The run window's "now" (last 5 minutes) or the detail screen's "mining time avg · whole run avg" —
    /// each caller builds its own text, this row only ever shows it.</summary>
    [ObservableProperty] private string _rateText;

    [ObservableProperty] private string? _rateTooltip;

    [ObservableProperty] private string _residueText;

    /// <summary>The residue's own ISK value, shown only on hover (ET-283) — residue never counts toward
    /// <see cref="IskText"/>, it left the rock but never reached the hold.</summary>
    [ObservableProperty] private string? _residueTooltip;

    /// <summary>▲▲ boosting (this character's own gamelog wrote the burst lines) or ▲ boosted (inferred from a local
    /// booster's log — never from a receiver's own log, which says nothing) — null where nothing is determinable,
    /// never "not boosted" (ET-283).</summary>
    [ObservableProperty] private string? _boostGlyph;

    [ObservableProperty] private string? _boostTooltip;

    public ObservableCollection<ActivityMiningRowViewModel> Ores { get; }

    public bool HasOres => Ores.Count > 0;

    /// <summary>Set only for a shared member whose client has not sent per-ore lines yet (ET-234's old shape) —
    /// "all ores · N units", no bar, no ISK. Null once the per-ore wire is there, and for this window's own rows.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFallbackTotalOnly))]
    private string? _fallbackText;

    public bool IsFallbackTotalOnly => FallbackText is not null;

    /// <summary>An external fleet member with no mining shared at all (ET-272's convention) — a name-only row, never
    /// counted in the total.</summary>
    public bool IsNotShared { get; }

    /// <summary>Whether <paramref name="fresh"/> is this same row worked out again — the same character in the same
    /// role — rather than a different row that only happens to share a character id.</summary>
    public bool CanTakeOver(MiningCharacterGroupViewModel fresh) =>
        CharacterId == fresh.CharacterId && IsLocal == fresh.IsLocal && IsNotShared == fresh.IsNotShared;

    /// <summary>Shows what <paramref name="fresh"/> says, keeping every ore line that did not change as the object it
    /// already is.</summary>
    public void TakeOver(MiningCharacterGroupViewModel fresh)
    {
        CharacterName = fresh.CharacterName;
        IskText = fresh.IskText;
        RateText = fresh.RateText;
        RateTooltip = fresh.RateTooltip;
        ResidueText = fresh.ResidueText;
        ResidueTooltip = fresh.ResidueTooltip;
        BoostGlyph = fresh.BoostGlyph;
        BoostTooltip = fresh.BoostTooltip;
        FallbackText = fresh.FallbackText;

        bool hadOres = HasOres;
        Ores.ReconcileTo([.. fresh.Ores.Select(ore => Ores.FirstOrDefault(shown => shown.ShowsSameAs(ore)) ?? ore)]);
        if (hadOres != HasOres)
            OnPropertyChanged(nameof(HasOres));
    }
}
