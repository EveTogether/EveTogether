using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Sde;

namespace EveUtils.Shared.Modules.Runs.Earnings;

/// <summary>The catalogue class of a looted type for the loot table (ET-519): a blueprint by its category, the others by
/// the SDE's meta group. Without the store every type is common and the table tells rare from common by frequency alone.</summary>
internal static class LootRarity
{
    public const int BlueprintCategoryId = 9;

    private const int StorylineMetaGroupId = 3;
    private const int FactionMetaGroupId = 4;
    private const int OfficerMetaGroupId = 5;
    private const int DeadspaceMetaGroupId = 6;

    public static Func<int, LootRarityClass> Of(ISdeAccessor sde)
    {
        if (!sde.IsAvailable)
            return _ => LootRarityClass.Common;

        Dictionary<int, LootRarityClass> cache = [];
        return typeId =>
        {
            if (!cache.TryGetValue(typeId, out LootRarityClass rarity))
                cache[typeId] = rarity = _Classify(sde, typeId);

            return rarity;
        };
    }

    public static LootRarityClass Classify(int? categoryId, int? metaGroupId) =>
        categoryId is BlueprintCategoryId
            ? LootRarityClass.Blueprint
            : metaGroupId switch
            {
                StorylineMetaGroupId => LootRarityClass.Storyline,
                FactionMetaGroupId => LootRarityClass.Faction,
                OfficerMetaGroupId => LootRarityClass.Officer,
                DeadspaceMetaGroupId => LootRarityClass.Deadspace,
                _ => LootRarityClass.Common
            };

    private static LootRarityClass _Classify(ISdeAccessor sde, int typeId)
    {
        if (sde.GetType(typeId) is not { } type)
            return LootRarityClass.Common;

        return Classify(sde.GetGroup(type.GroupId)?.CategoryId, sde.GetMetaGroupId(typeId));
    }
}
