using System.Collections.Generic;

namespace EveUtils.Client.ViewModels.Skills;

/// <summary>One attribute in the OPTIMISE remap table: the value, and whether the remap raises or lowers it against
/// "now" (+1 / -1; 0 for the "now" row and an unchanged value).</summary>
public sealed record AttributeCellViewModel(string Text, int Direction)
{
    public bool IsUp => Direction > 0;
    public bool IsDown => Direction < 0;
}

/// <summary>One OPTIMISE remap card (mockup v5, screen d): the CHA/INT/MEM/PER/WIL table "now" against "remap to",
/// the training time before and after with its end date, what it saves, and one sentence on why.</summary>
public sealed record OptimiseRemapCardViewModel(
    string Title,
    IReadOnlyList<AttributeCellViewModel> NowCells,
    IReadOnlyList<AttributeCellViewModel> RemapCells,
    string NowLabel,
    string NowText,
    string AfterText,
    string SavesText,
    string SavesPercentText,
    bool IsSmallGain,
    string Explanation);

/// <summary>One implant slot 1-5 on OPTIMISE: the slot, what is plugged in ("—" when empty) and the attribute that slot
/// raises.</summary>
public sealed record ImplantSlotViewModel(string SlotText, string ImplantText, string AttributeName, bool IsEmpty);
