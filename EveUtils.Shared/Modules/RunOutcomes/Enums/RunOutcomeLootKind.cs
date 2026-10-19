namespace EveUtils.Shared.Modules.RunOutcomes.Enums;

/// <summary>Which of an <see cref="Runs.Earnings.EarningsSample"/>'s item lists a shared line came from, so the server
/// keeps what the yield is computed from and nothing else.</summary>
public enum RunOutcomeLootKind
{
    Gained,
    Lost,

    /// <summary>A homefront's ore, part of its yield (ET-513).</summary>
    Mined
}
