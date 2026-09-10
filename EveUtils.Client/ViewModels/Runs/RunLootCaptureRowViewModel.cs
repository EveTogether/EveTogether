using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>One capture on the running run, as one line of the strip under the loot list. <see cref="RepeatOfNumber"/>
/// is set when this capture's hash matches an earlier one on the same run — the "identical to #1" the
/// momentopname-besluit calls for — and is worked out client-side from the whole list rather than stored, so it never
/// needs a migration of its own.</summary>
public sealed partial class RunLootCaptureRowViewModel : ObservableObject
{
    public Guid CaptureId { get; }
    public DateTime CapturedAtUtc { get; }
    public LootCaptureSource Source { get; }
    public int? RepeatOfNumber { get; }
    public IReadOnlyList<RunLootEntryDto> Entries { get; }

    /// <summary>Its place in the run, from 1 — what the section calls it everywhere else ("difference #2 → #4"), so
    /// the strip, the caption and the starting-hold picker name the same capture by the same number. It does the work
    /// the clock used to do, and does it better: "identical to #1" is read in one go where two timestamps have to be
    /// compared.</summary>
    public int Number { get; }

    public string NumberDisplay => $"#{Number}";

    /// <summary>Where its bytes came from, which is a different question from what it is to the run. Written out per
    /// member rather than as "Pasted or else clipboard": a source this does not know about has to be wrong out loud,
    /// not quietly called a clipboard copy.</summary>
    public string SourceText => Source switch
    {
        LootCaptureSource.Pasted => "PASTED",
        LootCaptureSource.Manual => "EDITED BY HAND",
        _ => "CLIPBOARD"
    };

    public string LineCountText => Entries.Count == 1 ? "1 row" : $"{Entries.Count} rows";

    /// <summary>The badge a repeat carries in the strip, counted or not: it names the copy it repeats.</summary>
    public string? RepeatBadgeText => RepeatOfNumber is { } number ? $"IDENTICAL TO #{number}" : null;

    /// <summary>EXCLUDED is said by the badge for an exclusion the pilot made; a repeat's own badge already says why it
    /// is out, and two badges for one reason is one too many.</summary>
    public bool IsExcludedBadgeShown => IsExcluded && RepeatOfNumber is null;

    /// <summary>A repeat is the one exclusion a pilot may want to argue with — he really did loot the same thing
    /// twice — so it is the one that carries a way back in. Every other exclusion is his own edit, and the way to
    /// undo that is to edit again.</summary>
    public bool CanReinclude => RepeatOfNumber is not null && IsExcluded;

    /// <summary>What this capture is to the run. Shown whether or not the paste boxes are: a pilot back on the
    /// clipboard way still has to be able to see which capture his figures are counted from.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RoleText))]
    [NotifyPropertyChangedFor(nameof(IsCargoBefore))]
    private LootCaptureRole _role;

    public string RoleText => Role switch
    {
        LootCaptureRole.CargoBefore => "STARTING HOLD",
        LootCaptureRole.CargoAfter => "ENDING HOLD",
        _ => "DURING THE RUN"
    };

    public bool IsCargoBefore => Role is LootCaptureRole.CargoBefore;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanReinclude))]
    [NotifyPropertyChangedFor(nameof(StateText))]
    [NotifyPropertyChangedFor(nameof(ReincludeText))]
    [NotifyPropertyChangedFor(nameof(IsExcludedBadgeShown))]
    private bool _isExcluded;

    public string CapturedAtText => CapturedAtUtc.ToLocalTime().ToString("HH:mm:ss");

    /// <summary>What the switch beside it stands at, in words — the colour of a switch alone is not a reason.</summary>
    public string StateText => !IsExcluded
        ? "counted"
        : RepeatOfNumber is { } number
            ? $"excluded — repeat of #{number}"
            : "excluded — a deliberate edit";

    /// <summary>The named way back in an excluded capture carries beside its switch (ET-215 mockup). A repeat's says
    /// what it is, because that is the one exclusion a pilot is expected to argue with.</summary>
    public string ReincludeText => RepeatOfNumber is null ? "re-include" : "re-include this repeat";

    /// <summary>What this capture came to on its own, whether or not it counts — the weight of the block, readable
    /// without opening it. Set by the section, which is where the prices are.</summary>
    [ObservableProperty] private string? _subtotalDisplay;

    /// <summary><see cref="SubtotalDisplay"/> without its unit, for the strip's money column.</summary>
    [ObservableProperty] private string? _subtotalAmountText;

    /// <summary>Its rows as they came in, each valued — set by the section, which holds the prices.</summary>
    [ObservableProperty] private IReadOnlyList<ActivityLootLineViewModel> _lines = [];

    /// <summary>It came in after the pilot's last edit, so its rows are under his list rather than in it. Derived
    /// from the order and never stored: it means "later than the hand-written capture", which the list already
    /// says.</summary>
    [ObservableProperty] private bool _isAddedAfterEdit;

    /// <summary>How the starting-hold picker names it, which is how the strip names it too.</summary>
    public string PickerText => $"{NumberDisplay} · {SourceText} · {LineCountText}";

    public RunLootCaptureRowViewModel(RunLootCaptureDto dto, int? repeatOfNumber, int number)
    {
        CaptureId = dto.CaptureId;
        CapturedAtUtc = dto.CapturedAtUtc;
        Source = dto.Source;
        Number = number;
        RepeatOfNumber = repeatOfNumber;
        Entries = dto.Entries;
        _role = dto.Role;
        _isExcluded = dto.IsExcluded;
    }
}
