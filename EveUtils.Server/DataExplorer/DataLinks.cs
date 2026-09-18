using EveUtils.Server.Components.DataList;

namespace EveUtils.Server.DataExplorer;

/// <summary>
/// Where each kind of record opens: its own list, with the record selected. The one place a link target is decided,
/// so a list that moves only changes this file.
/// </summary>
public static class DataLinks
{
    public const string CharactersPath = "/data/characters";
    public const string FleetsPath = "/data/fleets";
    public const string CompositionsPath = "/data/compositions";
    public const string SharedFitsPath = "/data/fits";
    public const string RunsPath = "/data/runs";
    public const string SessionsPath = "/data/sessions";

    public static string List(DataEntity entity) => entity switch
    {
        DataEntity.Characters => CharactersPath,
        DataEntity.Fleets => FleetsPath,
        DataEntity.Compositions => CompositionsPath,
        DataEntity.SharedFits => SharedFitsPath,
        DataEntity.Runs => RunsPath,
        DataEntity.Sessions => SessionsPath,
        _ => throw new ArgumentOutOfRangeException(nameof(entity), entity, "Every data entity has a list."),
    };

    public static string Character(int esiCharacterId) => _Selected(CharactersPath, esiCharacterId.ToString());
    public static string Fleet(long id) => _Selected(FleetsPath, id.ToString());
    public static string Composition(long id) => _Selected(CompositionsPath, id.ToString());
    public static string SharedFit(int id) => _Selected(SharedFitsPath, id.ToString());
    public static string RunGroup(string key) => _Selected(RunsPath, key);
    public static string Session(int id) => _Selected(SessionsPath, id.ToString());

    /// <summary>The Runs list filtered to one character's id, for a character that has runs but no paired row to open.</summary>
    public static string RunsOf(long characterId) => $"{RunsPath}?{DataListState.QueryParam}={characterId}";

    private static string _Selected(string path, string key) => $"{path}?{DataListState.SelParam}={Uri.EscapeDataString(key)}";
}
