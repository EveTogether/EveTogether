namespace EveUtils.Client.ViewModels.Activity;

/// <summary>The notices a compact run window can unfold in itself (ET-478). Only one is open at a time.</summary>
public enum CompactNotice
{
    None,
    OpenEscalation,
    LootPricing,
    TierWeather
}
