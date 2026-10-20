using EveUtils.Shared.Modules.Runs.Earnings;
using EveUtils.Shared.Modules.Sde;

namespace EveUtils.Client.Runs;

/// <summary>What the runs overview needs to offer the loot table (ET-519): the way to open it, the figures for the short
/// TOP DROPS of the detail, and which characters ask a server. Absent where no loot table is registered, and then none of
/// its buttons are on screen.</summary>
public sealed record LootTableAccess(ILootTableOpener Opener, ILootTableSource Source, IReadOnlyList<int> CharacterIds,
    ISdeAccessor? Sde)
{
    public void Open(ActivityStatsKey? key) => Opener.Open(key, CharacterIds);
}
