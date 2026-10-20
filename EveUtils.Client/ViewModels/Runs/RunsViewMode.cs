namespace EveUtils.Client.ViewModels.Runs;

/// <summary>What the runs list shows (ET-517). Appended only, so a stored value keeps its meaning.</summary>
public enum RunsViewMode
{
    /// <summary>One line per finished activity, grouped by day: the default, so the normal flow costs no click.</summary>
    Runs,

    /// <summary>One line per activity key with what it typically earns.</summary>
    ByActivity
}

/// <summary>What the BY ACTIVITY list is ordered by (ET-517).</summary>
public enum ActivityStatsSort
{
    LastFlown,
    Typical,
    PerHour,
    Runs
}
