namespace EveUtils.Server.Components.DataList;

/// <summary>One of the state buttons above a list. The first filter of a definition is the unfiltered default.</summary>
public sealed class DataListFilter<TRow>
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public required Func<TRow, bool> Matches { get; init; }
}
