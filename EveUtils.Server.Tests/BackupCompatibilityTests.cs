using EveUtils.Server.Backup;
using EveUtils.Server.Data;
using Xunit;

namespace EveUtils.Server.Tests;

/// <summary>
/// The rule the ticket asks for: an archive from a newer server is refused, an older one is allowed. Decided on the
/// migration set rather than the version string, so it holds whether or not anyone remembered to bump a version.
/// </summary>
public class BackupCompatibilityTests
{
    private static readonly string[] KnownMigrations =
        ["20260101000000_Init", "20260201000000_AddFleets", "20260301000000_AddBackupDownloadAudit"];

    [Fact]
    public void Check_ArchiveFromAnOlderServer_IsAllowed()
    {
        var manifest = Manifest(["20260101000000_Init"], "0.1.0");

        Assert.True(BackupCompatibility.Check(manifest, KnownMigrations, DatabaseProvider.Sqlite, "0.2.0").IsSuccess);
    }

    [Fact]
    public void Check_ArchiveFromTheSameServer_IsAllowed()
    {
        Assert.True(BackupCompatibility.Check(Manifest(KnownMigrations, "0.2.0"), KnownMigrations, DatabaseProvider.Sqlite, "0.2.0").IsSuccess);
    }

    [Fact]
    public void Check_ArchiveNamingAMigrationThisBuildDoesNotHave_IsRefused()
    {
        var manifest = Manifest([.. KnownMigrations, "20260401000000_AddSomethingNewer"], "0.3.0");

        var result = BackupCompatibility.Check(manifest, KnownMigrations, DatabaseProvider.Sqlite, "0.2.0");

        Assert.False(result.IsSuccess);
        Assert.Contains("20260401000000_AddSomethingNewer", result.Messages[0].Text, StringComparison.Ordinal);
    }

    /// <summary>A newer server whose migrations happen not to have moved is still refused on the format version.</summary>
    [Fact]
    public void Check_NewerArchiveFormat_IsRefused()
    {
        var manifest = Manifest(KnownMigrations, "9.9.9");
        manifest.FormatVersion = BackupFormat.ContentVersion + 1;

        Assert.False(BackupCompatibility.Check(manifest, KnownMigrations, DatabaseProvider.Sqlite, "0.2.0").IsSuccess);
    }

    /// <summary>
    /// Column values are stored in the shape the source engine uses, and the four migration stacks do not even
    /// share migration ids — so the provider has to match before either comparison means anything.
    /// </summary>
    [Fact]
    public void Check_ArchiveFromAnotherDatabaseEngine_IsRefused()
    {
        var manifest = Manifest(KnownMigrations, "0.2.0");
        manifest.Provider = DatabaseProvider.MySql;

        var result = BackupCompatibility.Check(manifest, KnownMigrations, DatabaseProvider.Sqlite, "0.2.0");

        Assert.False(result.IsSuccess);
        Assert.Contains("MySql", result.Messages[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_ArchiveWithoutMigrationState_IsRefused()
    {
        Assert.False(BackupCompatibility.Check(Manifest([], "0.2.0"), KnownMigrations, DatabaseProvider.Sqlite, "0.2.0").IsSuccess);
    }

    private static BackupManifest Manifest(IEnumerable<string> applied, string appVersion)
    {
        var list = applied.ToList();
        return new BackupManifest
        {
            AppVersion = appVersion,
            Provider = DatabaseProvider.Sqlite,
            Migrations = new BackupMigrationState { Applied = list, Target = list.LastOrDefault() },
        };
    }
}
