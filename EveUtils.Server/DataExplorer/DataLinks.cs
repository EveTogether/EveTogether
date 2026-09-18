namespace EveUtils.Server.DataExplorer;

/// <summary>
/// Where each kind of record opens. The one place a link target is decided, so when an entity moves onto its own list
/// only this file changes. Fleets have their list; characters, compositions, shared fits and sessions still live as
/// cards on <c>/data</c>, where each row carries an anchor id that the link scrolls to and highlights.
/// </summary>
public static class DataLinks
{
    public const string FleetsPath = "/data/fleets";
    public const string RecordsPath = "/data";
    public const string CharactersPath = RecordsPath + "#characters";
    public const string CompositionsPath = RecordsPath + "#compositions";
    public const string SharedFitsPath = RecordsPath + "#fits";
    public const string SessionsPath = RecordsPath + "#sessions";

    /// <summary>Where an entity's own list is, or null while it has none (runs, until ET-317).</summary>
    public static string? List(DataEntity entity) => entity switch
    {
        DataEntity.Characters => CharactersPath,
        DataEntity.Fleets => FleetsPath,
        DataEntity.Compositions => CompositionsPath,
        DataEntity.SharedFits => SharedFitsPath,
        DataEntity.Sessions => SessionsPath,
        _ => null,
    };

    public static string Fleet(long id) => $"{FleetsPath}?sel={id}";
    public static string Character(int esiCharacterId) => $"{RecordsPath}#character-{esiCharacterId}";
    public static string Composition(long id) => $"{RecordsPath}#composition-{id}";
    public static string SharedFit(int id) => $"{RecordsPath}#fit-{id}";
    public static string Session(int id) => $"{RecordsPath}#session-{id}";

    public static string CharacterAnchor(int esiCharacterId) => $"character-{esiCharacterId}";
    public static string CompositionAnchor(long id) => $"composition-{id}";
    public static string SharedFitAnchor(int id) => $"fit-{id}";
    public static string SessionAnchor(int id) => $"session-{id}";
}
