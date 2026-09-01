namespace EveUtils.Server.Data;

/// <summary>
/// Resolves the server data directory — the folder that holds the SQLite database, the TLS certificate, the
/// token-protector key, the app log, the ESI cache and the SDE. Together those are the server's permanent
/// identity, so the location has to survive a rebuild, a <c>dotnet clean</c> and a moved checkout (ET-94).
/// </summary>
internal static class ServerDataDirectory
{
    public const string EnvironmentVariableName = "EVEUTILS_SERVER_DATA_DIR";
    public const string ConfigurationKey = "Server:DataDirectory";

    /// <summary>Where every installation kept its data before ET-94: a folder inside the build output.</summary>
    public static string LegacyDirectory => Path.Combine(AppContext.BaseDirectory, "data");

    public static ServerDataLocation Resolve(string? environmentOverride, string? configuredPath)
    {
        if (environmentOverride is { Length: > 0 })
            return new ServerDataLocation(environmentOverride, ServerDataDirectorySource.EnvironmentVariable);

        if (configuredPath?.Trim() is { Length: > 0 } configured)
            return new ServerDataLocation(configured, ServerDataDirectorySource.Configuration);

        return new ServerDataLocation(_DefaultDirectory(), ServerDataDirectorySource.Default);
    }

    /// <summary>
    /// Moves a pre-ET-94 installation out of the build output into <paramref name="location"/>, once, on startup.
    /// Only for <see cref="ServerDataDirectorySource.Default"/>: an explicit env-var or config path is a deliberate
    /// choice, and the headless suites point the env-var at a throwaway folder (Makefile) — migrating into that
    /// would drag the developer's real installation into a directory `make clean-test-data` deletes.
    /// </summary>
    public static ServerDataMigration MigrateLegacyContents(ServerDataLocation location, string legacyDirectory)
    {
        if (location.Source is not ServerDataDirectorySource.Default || !Directory.Exists(legacyDirectory))
            return ServerDataMigration.None;

        // Anything already in the target is an installation of its own; never merge two data directories.
        if (Directory.Exists(location.Path) && Directory.EnumerateFileSystemEntries(location.Path).Any())
            return ServerDataMigration.None;

        Directory.CreateDirectory(location.Path);

        var moved = new List<string>();
        var leftBehind = new List<string>();

        foreach (var file in OrderForMove(Directory.GetFiles(legacyDirectory)))
        {
            var name = Path.GetFileName(file);
            File.Move(file, Path.Combine(location.Path, name));
            moved.Add(name);
        }

        foreach (var directory in Directory.GetDirectories(legacyDirectory))
        {
            var name = Path.GetFileName(directory);
            try
            {
                Directory.Move(directory, Path.Combine(location.Path, name));
                moved.Add(name);
            }
            catch (IOException)
            {
                // esi-cache/ and sde/ are the only directories here and both rebuild themselves, so a move that
                // cannot work (Directory.Move across volumes, e.g. a checkout on another drive than the user
                // profile) is reported and skipped rather than failing the migration of the data that is unique.
                leftBehind.Add(name);
            }
        }

        return new ServerDataMigration(moved, leftBehind);
    }

    /// <summary>
    /// Puts the token-protector key last. A move that fails halfway must not be able to leave the key in the new
    /// location without the database it decrypts — the server would then come up on a fresh, empty database as if
    /// nothing were wrong. The other way round (database moved, key left behind) trips the new-identity guard,
    /// which is loud and recoverable.
    /// </summary>
    public static IEnumerable<string> OrderForMove(IEnumerable<string> files) =>
        files.OrderBy(file => Path.GetExtension(file).Equals(".key", StringComparison.OrdinalIgnoreCase));

    // Anchored to the per-user data folder, like the client's ClientServices.DataDirectory(). Not the working
    // directory: `dotnet run` and Rider start the server from different ones, which is what anchoring to the
    // binary originally fixed (d3cfa5f) — and not the build output either, which is what ET-94 is about.
    // A sibling of the client's "EveUtils" rather than a folder inside it: the client's subfolders are its
    // EVEUTILS_INSTANCE namespace, so an instance could otherwise be pointed at the server's data.
    private static string _DefaultDirectory()
    {
        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(localApplicationData))
        {
            throw new InvalidOperationException(
                "Cannot determine the per-user data folder (no HOME/LOCALAPPDATA), so there is no safe default " +
                $"for the server data directory. Set {EnvironmentVariableName} or {ConfigurationKey} to an " +
                "explicit path.");
        }

        return Path.Combine(localApplicationData, "EveUtils.Server");
    }
}
