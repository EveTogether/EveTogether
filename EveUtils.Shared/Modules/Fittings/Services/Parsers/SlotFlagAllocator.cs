using EveUtils.Shared.Modules.Sde.Enums;
using EveUtils.Shared.Modules.Sde.Dtos;

namespace EveUtils.Shared.Modules.Fittings.Services.Parsers;

/// <summary>
/// Hands out ESI slot flags (<c>HiSlot0</c>, <c>MedSlot1</c>, …) per slot category, incrementing within each
/// category, so an assembled fit matches the ESI fitting shape the rest of the app already understands.
/// </summary>
internal sealed class SlotFlagAllocator
{
    private readonly Dictionary<SdeSlotType, int> _counters = new();

    public string Allocate(SdeSlotType slot)
    {
        var index = _counters.TryGetValue(slot, out var current) ? current : 0;
        _counters[slot] = index + 1;
        return Prefix(slot) + index;
    }

    private static string Prefix(SdeSlotType slot) => slot switch
    {
        SdeSlotType.High => "HiSlot",
        SdeSlotType.Medium => "MedSlot",
        SdeSlotType.Low => "LoSlot",
        SdeSlotType.Rig => "RigSlot",
        SdeSlotType.Subsystem => "SubSystemSlot",
        SdeSlotType.Service => "ServiceSlot",
        _ => "Cargo"
    };
}
