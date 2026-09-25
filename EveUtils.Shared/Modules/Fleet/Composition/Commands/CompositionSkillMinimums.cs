using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Skills;

namespace EveUtils.Shared.Modules.Fleet.Composition.Commands;

/// <summary>Validation and entity mapping for the skill minimums the Add/Edit entry commands carry.</summary>
internal static class CompositionSkillMinimums
{
    /// <summary>The first problem with <paramref name="minimums"/>, or null when every row names a skill once at
    /// level I–V. A skill twice would collide on the (EntryId, SkillTypeId) key.</summary>
    public static ResultMessage? Validate(IReadOnlyList<SkillMinimum>? minimums)
    {
        if (minimums is null)
        {
            return null;
        }

        if (minimums.Any(m => m.SkillTypeId <= 0 || m.Level is < 1 or > 5))
        {
            return new ResultMessage(MessageSeverity.Error, MessageCodes.ValidationFailed,
                "A skill minimum needs a skill and a level from I to V.", "FleetComposition");
        }

        if (minimums.Select(m => m.SkillTypeId).Distinct().Count() != minimums.Count)
        {
            return new ResultMessage(MessageSeverity.Error, MessageCodes.ValidationFailed,
                "A skill can only have one minimum per fit.", "FleetComposition");
        }

        return null;
    }

    public static List<FleetCompositionEntrySkillMinimum> ToEntities(IReadOnlyList<SkillMinimum>? minimums) =>
        (minimums ?? []).Select(m => new FleetCompositionEntrySkillMinimum { SkillTypeId = m.SkillTypeId, Level = m.Level }).ToList();
}
