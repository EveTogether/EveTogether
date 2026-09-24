namespace EveUtils.Client.Composition;

/// <summary>
/// Where the client keeps its data. The folder is <c>EveTogetherData</c>, deliberately not <c>EveTogether</c>:
/// Velopack installs the app into <c>%LOCALAPPDATA%\EveTogether</c> (packId) and wipes that folder on uninstall.
/// </summary>
public static class ClientDataLocation
{
    public const string FolderName = "EveTogetherData";
    public const string LegacyFolderName = "EveUtils";
    public const string InstanceVariable = "EVETOGETHER_INSTANCE";
    public const string LegacyInstanceVariable = "EVEUTILS_INSTANCE";

    private static readonly Lazy<DataMigration> Resolved = new(() =>
    {
        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Resolve(Path.Combine(localApplicationData, LegacyFolderName), Path.Combine(localApplicationData, FolderName));
    });

    /// <summary>Points the client at a scratch root instead of the player's folder; set once, before the first read.</summary>
    internal static string? RootOverride { get; set; }

    /// <summary>The player's default root, ignoring <see cref="RootOverride"/>; for read-only lookups of real data.</summary>
    internal static string DefaultRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), FolderName);

    /// <summary>What this run did about the legacy folder; resolving it is what performs the move.</summary>
    public static DataMigration Migration => Resolved.Value;

    public static string Root => RootOverride ?? Migration.Root;

    /// <summary>The instance name, letting two clients share one machine; the old variable still works.</summary>
    public static string? InstanceName()
    {
        var name = Environment.GetEnvironmentVariable(InstanceVariable)?.Trim();
        if (string.IsNullOrEmpty(name))
            name = Environment.GetEnvironmentVariable(LegacyInstanceVariable)?.Trim();
        return string.IsNullOrEmpty(name) ? null : name;
    }

    /// <summary>
    /// Renames the legacy folder to the new one, and only that: one rename either happens or does not, so the data
    /// never ends up split over two folders. A folder that cannot be renamed (another client holds a file open)
    /// keeps the app on the legacy folder for this run; the next start tries again.
    /// </summary>
    public static DataMigration Resolve(string legacyRoot, string root)
    {
        if (Directory.Exists(root) || !Directory.Exists(legacyRoot))
            return new DataMigration(root, DataMigrationOutcome.NotNeeded);

        try
        {
            Directory.Move(legacyRoot, root);
            return new DataMigration(root, DataMigrationOutcome.Moved);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new DataMigration(legacyRoot, DataMigrationOutcome.Failed, ex);
        }
    }
}
