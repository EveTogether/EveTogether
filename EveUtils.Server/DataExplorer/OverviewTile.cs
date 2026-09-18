namespace EveUtils.Server.DataExplorer;

/// <summary>One count tile on the Dashboard: the total, how it splits by state, and how many of them need an admin.</summary>
public sealed class OverviewTile
{
    public required int Total { get; init; }
    public required IReadOnlyList<TileSegment> Segments { get; init; }

    /// <summary>Attention items of Problem or Warning severity that point into this entity's list.</summary>
    public int NeedsAttention { get; init; }
}
