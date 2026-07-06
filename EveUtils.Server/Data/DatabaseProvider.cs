namespace EveUtils.Server.Data;

/// <summary>
/// Database engines supported by the server. Chosen via <c>Database:Provider</c>.
/// (The client always runs on SQLite and does not have this choice.)
/// </summary>
public enum DatabaseProvider
{
    Sqlite,
    MySql,
    SqlServer,
    PostgreSql
}
