using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Entities;

public sealed class RunParameter
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public RunParameterKey ParameterKey { get; set; }

    /// <summary>The raw text as it was seen. Kept beside <see cref="Amount"/> rather than replaced by it: if the
    /// parse is ever wrong, this is the only way back to what the player actually had in front of them.</summary>
    public string TypedValue { get; set; } = string.Empty;

    /// <summary>How much, or null when the observation has no quantity at all ("there was an escalation"). Numeric
    /// so a reward total is a SUM in SQL instead of text parsed in the player's locale (ET-137).</summary>
    public decimal? Amount { get; set; }

    /// <summary>Set only when the reward is an object, so the item keeps both its count and its type.</summary>
    public int? ItemTypeId { get; set; }

    /// <summary>The deadline a bonus reward carries, in seconds (the SDE's <c>bonusTimeInterval</c>). A property of
    /// this one row, not of the run: the row is prefilled when the mission is chosen, so it stands whether or not
    /// the bonus was earned, and "did I make it" is the run's duration against this number.</summary>
    public int? BonusWindowSeconds { get; set; }

    /// <summary>Ties together the rows that describe one entry of a repeatable observation — a run can lead to more
    /// than one escalation, each written as its own set of <c>Escalation*</c> rows (ET-451). Null on every other
    /// row, and on an escalation registered before this column existed, which reads as the run's one escalation.</summary>
    public Guid? EntryId { get; set; }

    public DateTime ObservedAtUtc { get; set; }

    /// <summary>The unit price of the type this row names, fixed the way <see cref="RunLootEntry.UnitPriceIsk"/> is
    /// (ET-463). Only the filament's type row carries one; on every other row it stays null.</summary>
    public decimal? UnitPriceIsk { get; set; }

    public DateTime? PricedAtUtc { get; set; }
    public PriceSnapshotSource? PriceSource { get; set; }
    public Run? Run { get; set; }
}
