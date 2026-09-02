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

    public DateTime ObservedAtUtc { get; set; }
    public Run? Run { get; set; }
}
