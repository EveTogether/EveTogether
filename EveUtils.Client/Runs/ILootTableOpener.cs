using EveUtils.Shared.Modules.Runs.Earnings;

namespace EveUtils.Client.Runs;

/// <summary>The one way into the loot table window (ET-519): the earnings panel, the runs overview and the range line all
/// ask it, so each is a single click and none builds the window itself.</summary>
public interface ILootTableOpener
{
    /// <summary>Opens the window on <paramref name="key"/>, or with none on the activities the pilot has flown. An open
    /// window is reused: a second call only swaps the key.</summary>
    void Open(ActivityStatsKey? key, IReadOnlyList<int> characterIds);
}
