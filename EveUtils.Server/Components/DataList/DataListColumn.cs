using Microsoft.AspNetCore.Components;

namespace EveUtils.Server.Components.DataList;

public sealed class DataListColumn<TRow>
{
    /// <summary>Stable key used in the <c>sort</c> query parameter, so it must not change once a URL has been shared.</summary>
    public required string Key { get; init; }
    public required string Label { get; init; }
    public required RenderFragment<TRow> Cell { get; init; }

    /// <summary>Null leaves the column unsortable.</summary>
    public Func<TRow, IComparable?>? SortBy { get; init; }

    /// <summary>Counts and recency read best largest/newest first; names read best A→Z.</summary>
    public bool SortDescendingFirst { get; init; }

    public bool Numeric { get; init; }
    public bool HideWhenNarrow { get; init; }
}
