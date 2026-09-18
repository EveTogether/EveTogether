namespace EveUtils.Server.Components.DataList;

public sealed class DataListGrouping<TRow>
{
    public required string Key { get; init; }
    public required string Label { get; init; }

    /// <summary>Null means "no grouping": the rows form one flat list.</summary>
    public Func<TRow, string>? GroupOf { get; init; }

    /// <summary>Orders the groups when their natural order is not alphabetical (fleet status: in op before archived).
    /// Null orders the groups by name.</summary>
    public Func<TRow, int>? Rank { get; init; }

    /// <summary>A line about the whole group on its header ("12 runs · 3h 40m flown"), over every row in the group that
    /// passes the filter, not just the ones on this page. Null shows the count only.</summary>
    public Func<IReadOnlyList<TRow>, string>? Summary { get; init; }
}
