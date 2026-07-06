using System;

namespace EveUtils.Client.ViewModels.FitBrowser;

/// <summary>
/// Classifies a raw ESI fitting <c>flag</c> (e.g. <c>HiSlot0</c>, <c>LoSlot2</c>, <c>DroneBay</c>) into a
/// <see cref="FitSlotCategory"/> for the detail slot-list. The trailing slot index is preserved as-is so
/// intentional gaps between modules are never collapsed or renumbered (plan §9).
/// </summary>
public static class FitSlotClassifier
{
    public static FitSlotCategory Classify(string flag)
    {
        if (flag.StartsWith("HiSlot", StringComparison.OrdinalIgnoreCase)) return FitSlotCategory.High;
        if (flag.StartsWith("MedSlot", StringComparison.OrdinalIgnoreCase)) return FitSlotCategory.Medium;
        if (flag.StartsWith("LoSlot", StringComparison.OrdinalIgnoreCase)) return FitSlotCategory.Low;
        if (flag.StartsWith("RigSlot", StringComparison.OrdinalIgnoreCase)) return FitSlotCategory.Rig;
        if (flag.StartsWith("SubSystemSlot", StringComparison.OrdinalIgnoreCase)) return FitSlotCategory.Subsystem;
        if (flag.StartsWith("ServiceSlot", StringComparison.OrdinalIgnoreCase)) return FitSlotCategory.Service;
        if (flag.StartsWith("DroneBay", StringComparison.OrdinalIgnoreCase)) return FitSlotCategory.Drone;
        if (flag.StartsWith("FighterBay", StringComparison.OrdinalIgnoreCase) ||
            flag.StartsWith("FighterTube", StringComparison.OrdinalIgnoreCase)) return FitSlotCategory.Fighter;
        if (flag.StartsWith("Cargo", StringComparison.OrdinalIgnoreCase)) return FitSlotCategory.Cargo;
        return FitSlotCategory.Other;
    }

    /// <summary>The slot index encoded in the flag (e.g. <c>LoSlot2</c> → 2); 0 when the flag carries no index.</summary>
    public static int SlotIndex(string flag)
    {
        var i = flag.Length;
        while (i > 0 && char.IsDigit(flag[i - 1])) i--;
        return i < flag.Length && int.TryParse(flag.AsSpan(i), out var index) ? index : 0;
    }

    /// <summary>Header label shown for a slot group in the detail list.</summary>
    public static string Label(FitSlotCategory category) => category switch
    {
        FitSlotCategory.High => "HIGH SLOTS",
        FitSlotCategory.Medium => "MID SLOTS",
        FitSlotCategory.Low => "LOW SLOTS",
        FitSlotCategory.Rig => "RIGS",
        FitSlotCategory.Subsystem => "SUBSYSTEMS",
        FitSlotCategory.Service => "SERVICE SLOTS",
        FitSlotCategory.Drone => "DRONE BAY",
        FitSlotCategory.Fighter => "FIGHTER BAY",
        FitSlotCategory.Cargo => "CARGO",
        _ => "OTHER"
    };
}
