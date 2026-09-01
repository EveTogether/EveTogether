using EveUtils.Server.Data;
using EveUtils.Shared.Messaging;

namespace EveUtils.Server.Backup;

/// <summary>
/// Decides whether this build may restore a given archive, before anything is dropped.
///
/// The test is the migration set, not the version string. An archive naming a migration this build does not have
/// came from a newer server, and its rows would be inserted into a schema that has not caught up yet — refused.
/// The other direction is fine and is the normal case: an older archive rebuilds the schema at its own migration
/// state, and the startup migration then brings it forward. That makes the check independent of whether anyone
/// remembered to bump a version number.
/// </summary>
internal static class BackupCompatibility
{
    public static Result Check(
        BackupManifest manifest,
        IReadOnlyCollection<string> knownMigrations,
        DatabaseProvider currentProvider,
        string currentAppVersion)
    {
        if (manifest.FormatVersion > BackupFormat.ContentVersion)
        {
            return _Incompatible(
                $"This archive uses backup format {manifest.FormatVersion}; this server reads up to " +
                $"{BackupFormat.ContentVersion}. It was made by a newer version of EVE Together " +
                $"({manifest.AppVersion}) — upgrade this server first, then restore.");
        }

        if (manifest.Provider != currentProvider)
        {
            return _Incompatible(
                $"This archive was taken from a {manifest.Provider} database and this server runs on " +
                $"{currentProvider}. Column values are stored in the shape the source engine uses, so they cannot " +
                "be moved between engines by a restore. Point this server at a " +
                $"{manifest.Provider} database, or take a fresh archive on {currentProvider}.");
        }

        if (manifest.Migrations.Target is null || manifest.Migrations.Applied.Count == 0)
        {
            return Result.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.BackupCorrupt,
                "This archive records no database migration state, so there is no schema to rebuild it into."));
        }

        var known = knownMigrations.ToHashSet(StringComparer.Ordinal);
        var missing = manifest.Migrations.Applied.Where(m => !known.Contains(m)).ToList();
        if (missing.Count > 0)
        {
            return _Incompatible(
                $"This archive was made by a newer version of EVE Together ({manifest.AppVersion}; this server is " +
                $"{currentAppVersion}). It expects {missing.Count} database migration(s) this build does not have, " +
                $"starting at '{missing[0]}'. Upgrade this server to that version or later, then restore. " +
                "Restoring an older archive on a newer server is supported; the other way round is not.");
        }

        return Result.Success();
    }

    private static Result _Incompatible(string text) =>
        Result.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.BackupIncompatible, text));
}
