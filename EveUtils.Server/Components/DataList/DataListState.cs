using Microsoft.AspNetCore.WebUtilities;

namespace EveUtils.Server.Components.DataList;

/// <summary>
/// A list's whole view state, read from and written back to the query string so a shared URL opens exactly the same
/// view: <c>?sel=12&amp;sort=-last&amp;filter=inop&amp;group=owner&amp;q=metal&amp;page=2</c>. A value equal to the
/// definition's default is left out of the URL, which keeps the plain list URL clean without changing what it opens.
/// </summary>
public sealed record DataListState(string? Sel, string Sort, string Filter, string Group, string Query, int Page)
{
    public const string SelParam = "sel";
    public const string SortParam = "sort";
    public const string FilterParam = "filter";
    public const string GroupParam = "group";
    public const string QueryParam = "q";
    public const string PageParam = "page";

    public string SortKey => Sort.TrimStart('-');
    public bool SortDescending => Sort.StartsWith('-');

    public static DataListState Parse<TRow>(string uri, DataListDefinition<TRow> definition)
    {
        var query = QueryHelpers.ParseQuery(new Uri(uri).Query);
        string? Read(string name) => query.TryGetValue(name, out var values) && !string.IsNullOrWhiteSpace(values.ToString())
            ? values.ToString()
            : null;

        var sort = Read(SortParam);
        if (sort is null || definition.Columns.All(c => c.Key != sort.TrimStart('-') || c.SortBy is null))
            sort = definition.DefaultSort;

        var filter = Read(FilterParam);
        if (filter is null || definition.Filters.All(f => f.Key != filter))
            filter = definition.Filters[0].Key;

        var group = Read(GroupParam);
        if (group is null || definition.Groupings.All(g => g.Key != group))
            group = definition.DefaultGrouping;

        var page = int.TryParse(Read(PageParam), out var parsed) && parsed > 1 ? parsed : 1;
        return new DataListState(Read(SelParam), sort, filter, group, Read(QueryParam) ?? string.Empty, page);
    }

    /// <summary>The query parameters for this state, with defaults as null so <c>GetUriWithQueryParameters</c> drops them.</summary>
    public IReadOnlyDictionary<string, object?> ToQuery<TRow>(DataListDefinition<TRow> definition) => new Dictionary<string, object?>
    {
        [SelParam] = Sel,
        [SortParam] = Sort == definition.DefaultSort ? null : Sort,
        [FilterParam] = Filter == definition.Filters[0].Key ? null : Filter,
        [GroupParam] = Group == definition.DefaultGrouping ? null : Group,
        [QueryParam] = Query.Length == 0 ? null : Query,
        [PageParam] = Page <= 1 ? null : Page,
    };
}
