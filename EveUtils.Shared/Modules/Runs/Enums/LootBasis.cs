namespace EveUtils.Shared.Modules.Runs.Enums;

/// <summary>What a run's loot was counted from (ET-513, ET-519). Ordered from least to most complete, so the weakest
/// run of an activity says what the activity as a whole can be trusted for.</summary>
public enum LootBasis
{
    /// <summary>Nothing was counted: never pasted, or a starting hold with no ending hold yet. Not "nothing dropped".</summary>
    None,

    /// <summary>Loose pastes without a starting hold; the last one may have been only part of the loot.</summary>
    Captures,

    /// <summary>The difference between a starting and an ending cargo hold: the complete loot of the run.</summary>
    CargoDifference
}
