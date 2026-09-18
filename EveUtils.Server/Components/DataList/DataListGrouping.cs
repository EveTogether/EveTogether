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
}
