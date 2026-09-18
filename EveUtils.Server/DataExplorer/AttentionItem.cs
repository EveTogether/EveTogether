namespace EveUtils.Server.DataExplorer;

/// <summary>One stale or orphaned record on the Needs attention list, with the link that opens it.</summary>
public sealed class AttentionItem
{
    public required AttentionKind Kind { get; init; }
    public required AttentionSeverity Severity { get; init; }
    public required DataEntity Entity { get; init; }
    public required string Title { get; init; }
    public required string Detail { get; init; }

    /// <summary>The record in its own list. Null while the entity has no list to land on (runs, until ET-317).</summary>
    public string? Href { get; init; }
}
