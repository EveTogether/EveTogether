namespace EveUtils.Shared.Modules.Killmails;

/// <summary>
/// Raw ESI killmail inventory flag numbers, translated to the ESI-fitting-style slot names the client's
/// <c>FitSlotClassifier</c> and <c>FitDetailWindowViewModel.BuildSlots</c> already read (ET-333). The SDE carries no
/// invFlags table [measured: build 3539543, none of its 102 files], so this is a fixed table of the flags a killmail
/// actually uses rather than the full CCP list — an unmapped flag is null, shown as OTHER and left out of the
/// reconstructed fit.
/// </summary>
public static class KillmailFlags
{
    public const int Cargo = 5;
    public const int DroneBay = 87;
    public const int Booster = 88;
    public const int Implant = 89;
    public const int FighterBay = 158;

    public static string? NameOf(int flag) => flag switch
    {
        >= 11 and <= 18 => $"LoSlot{flag - 11}",
        >= 19 and <= 26 => $"MedSlot{flag - 19}",
        >= 27 and <= 34 => $"HiSlot{flag - 27}",
        >= 92 and <= 99 => $"RigSlot{flag - 92}",
        >= 125 and <= 132 => $"SubSystemSlot{flag - 125}",
        >= 164 and <= 171 => $"ServiceSlot{flag - 164}",
        >= 159 and <= 163 => $"FighterTube{flag - 159}",
        DroneBay => "DroneBay",
        FighterBay => "FighterBay",
        Cargo => "Cargo",
        Implant => "Implant",
        Booster => "Booster",
        _ => null
    };
}
