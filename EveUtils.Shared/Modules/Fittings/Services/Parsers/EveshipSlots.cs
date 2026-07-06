namespace EveUtils.Shared.Modules.Fittings.Services.Parsers;

/// <summary>
/// Maps between eveship.fit v3 slot-type names and our ESI slot-flag prefixes. eveship v3 uses
/// High/Medium/Low/Rig/SubSystem; ESI fitting flags use HiSlot/MedSlot/LoSlot/RigSlot/SubSystemSlot + an index.
/// </summary>
internal static class EveshipSlots
{
    /// <summary>eveship v3 slot-type → our ESI flag prefix (null = unknown / unsupported, e.g. Service).</summary>
    public static string? FlagPrefix(string eveshipSlotType) => eveshipSlotType switch
    {
        "High" => "HiSlot",
        "Medium" => "MedSlot",
        "Low" => "LoSlot",
        "Rig" => "RigSlot",
        "SubSystem" => "SubSystemSlot",
        _ => null
    };

    /// <summary>Our ESI flag prefix → eveship v3 slot-type (null = no eveship equivalent, e.g. ServiceSlot).</summary>
    public static string? SlotType(string flagPrefix) => flagPrefix switch
    {
        "HiSlot" => "High",
        "MedSlot" => "Medium",
        "LoSlot" => "Low",
        "RigSlot" => "Rig",
        "SubSystemSlot" => "SubSystem",
        _ => null
    };
}
