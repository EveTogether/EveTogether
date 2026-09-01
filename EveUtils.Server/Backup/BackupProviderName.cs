using EveUtils.Server.Data;

namespace EveUtils.Server.Backup;

/// <summary>
/// Maps EF's runtime provider name onto <see cref="DatabaseProvider"/>. Taken from the live context rather than
/// re-read from <c>Database:Provider</c>, so what the archive records is the engine the rows actually came out of
/// even if the configuration has since been edited.
/// </summary>
internal static class BackupProviderName
{
    public static DatabaseProvider Resolve(string? providerName)
    {
        if (string.IsNullOrEmpty(providerName))
            throw new InvalidOperationException("The database context reports no provider, so a backup cannot record one.");

        if (providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            return DatabaseProvider.Sqlite;
        if (providerName.Contains("SqlServer", StringComparison.OrdinalIgnoreCase))
            return DatabaseProvider.SqlServer;
        if (providerName.Contains("MySql", StringComparison.OrdinalIgnoreCase))
            return DatabaseProvider.MySql;
        if (providerName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase)
            || providerName.Contains("PostgreSQL", StringComparison.OrdinalIgnoreCase))
        {
            return DatabaseProvider.PostgreSql;
        }

        throw new InvalidOperationException(
            $"Database provider '{providerName}' is not one of the four this server backs up. Add it to " +
            $"{nameof(DatabaseProvider)} and give it restore behaviour in {nameof(BackupIdentityInsert)} first.");
    }
}
