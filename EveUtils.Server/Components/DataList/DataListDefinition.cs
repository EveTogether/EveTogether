namespace EveUtils.Server.Components.DataList;

/// <summary>
/// Everything that makes one entity's list what it is. <see cref="DataList{TRow}"/> knows nothing about any entity:
/// a new list is a new definition plus a detail fragment, never a change to the component.
/// </summary>
public sealed class DataListDefinition<TRow>
{
    public required string Title { get; init; }

    /// <summary>Singular noun shown in the detail pane's header bar ("Fleet").</summary>
    public required string Kind { get; init; }

    public required Func<TRow, string> KeyOf { get; init; }
    public required IReadOnlyList<DataListColumn<TRow>> Columns { get; init; }
    public required IReadOnlyList<DataListFilter<TRow>> Filters { get; init; }
    public required IReadOnlyList<DataListGrouping<TRow>> Groupings { get; init; }
    public required string DefaultGrouping { get; init; }

    /// <summary>Column key, prefixed with <c>-</c> for descending, in the same form as the <c>sort</c> query parameter.</summary>
    public required string DefaultSort { get; init; }

    /// <summary>The text the filter box matches against: names and the ids behind them, so a pasted id finds its row.</summary>
    public required Func<TRow, string> SearchTextOf { get; init; }

    public string EmptyText { get; init; } = "Nothing here yet.";
    public string SelectPrompt { get; init; } = "Select a row to see it with everything it relates to.";
    public int PageSize { get; init; } = 50;
}
