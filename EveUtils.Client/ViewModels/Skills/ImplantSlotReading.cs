using System.Collections.Generic;
using System.Linq;
using EveUtils.Shared.Modules.Dogma;
using EveUtils.Shared.Modules.Sde;

namespace EveUtils.Client.ViewModels.Skills;

/// <summary>What a character has plugged in, by slot, from the SDE: the attribute enhancers in slots 1-5 (with their
/// bonus) and how many hardwirings sit in 6-10. Shared by OPTIMISE's slot row and the detail pane's implant line.</summary>
public sealed record ImplantSlotReading(IReadOnlyDictionary<int, (int TypeId, double Bonus)> AttributeSlots, int Hardwirings)
{
    public static ImplantSlotReading Read(IReadOnlyList<int> implantTypeIds, IDogmaDataAccessor dogma)
    {
        var bySlot = new Dictionary<int, (int TypeId, double Bonus)>();
        int hardwirings = 0;
        foreach (var typeId in implantTypeIds)
        {
            var attributes = dogma.GetBaseAttributes(typeId);
            int slot = (int)(attributes.FirstOrDefault(a => a.AttributeId == DogmaAttributeIds.Implantness)?.Value ?? 0);
            double bonus = attributes.Where(a => a.AttributeId is >= DogmaAttributeIds.CharismaBonus and <= DogmaAttributeIds.WillpowerBonus)
                .Select(a => a.Value).DefaultIfEmpty(0).Max();
            if (slot is >= 1 and <= 5 && bonus > 0)
            {
                bySlot[slot] = (typeId, bonus);
            }
            else if (slot > 5)
            {
                hardwirings++;
            }
        }

        return new ImplantSlotReading(bySlot, hardwirings);
    }

    /// <summary>"No attribute implants plugged in (4 hardwirings, none on attributes)." or "+4 attribute implants in
    /// slots 1–5 (2 hardwirings)." — the line under TRAINING RATE.</summary>
    public string Note
    {
        get
        {
            string wired = Hardwirings == 1 ? "1 hardwiring" : $"{Hardwirings} hardwirings";
            if (AttributeSlots.Count == 0)
            {
                return Hardwirings > 0
                    ? $"No attribute implants plugged in ({wired}, none on attributes)."
                    : "No attribute implants plugged in.";
            }

            var bonuses = AttributeSlots.Values.Select(v => v.Bonus).Distinct().OrderBy(b => b).Select(b => $"+{b:0}");
            string attributeText = $"{string.Join("/", bonuses)} attribute implants in {AttributeSlots.Count} of slots 1–5";
            return Hardwirings > 0 ? $"{attributeText} ({wired})." : $"{attributeText}.";
        }
    }
}
