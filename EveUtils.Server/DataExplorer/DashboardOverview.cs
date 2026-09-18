namespace EveUtils.Server.DataExplorer;

/// <summary>Everything the Dashboard shows except what moves every couple of seconds.</summary>
public sealed class DashboardOverview
{
    public required OverviewTile Characters { get; init; }
    public required OverviewTile Fleets { get; init; }
    public required OverviewTile Compositions { get; init; }
    public required OverviewTile SharedFits { get; init; }
    public required OverviewTile RunGroups { get; init; }
    public required OverviewTile Sessions { get; init; }
    public required IReadOnlyList<AttentionItem> Attention { get; init; }

    /// <summary>Individual runs across all groups, for the run tile's caption.</summary>
    public required int RunCount { get; init; }
}
