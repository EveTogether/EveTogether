namespace EveUtils.Shared.Modules.Runs.Enums;

/// <summary>How much of the pocket was opened, as the run stores it. Kept as a value rather than as the words on
/// the chips, so rewording a label never reaches a run that was already saved; members are only ever appended.
/// Each <see cref="ActivityKind"/> loots in its own vocabulary and is offered only its own members.</summary>
public enum RunLootStrategy
{
    BioadaptiveOnly,
    BioadaptiveAndTriglavian,
    AllCans,
    Blitzed,
    Cleared,
    FullClear,

    /// <summary>Only the worthwhile containers were done and the rest left standing. A site's word: it says you
    /// chose, where "partially" would only say you did not get through it.</summary>
    CherryPicked
}
