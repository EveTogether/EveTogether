using EveUtils.Server.Data;
using Xunit;

namespace EveUtils.Server.Tests;

/// <summary>
/// Server data-directory resolution and the one-off move out of the build output (ET-94): the override order,
/// a default that survives a rebuild, and the conditions under which an existing installation is relocated.
/// </summary>
public class ServerDataDirectoryTests : IDisposable
{
    private readonly string _root;
    private readonly string _legacy;
    private readonly string _target;

    public ServerDataDirectoryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "et94-" + Guid.NewGuid().ToString("N"));
        _legacy = Path.Combine(_root, "legacy");
        _target = Path.Combine(_root, "target");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Resolve_EnvironmentVariableSet_WinsOverConfiguration()
    {
        var location = ServerDataDirectory.Resolve("/from-env", "/from-config");

        Assert.Equal("/from-env", location.Path);
        Assert.Equal(ServerDataDirectorySource.EnvironmentVariable, location.Source);
    }

    [Fact]
    public void Resolve_ConfigurationOnly_UsesConfiguration()
    {
        var location = ServerDataDirectory.Resolve(null, "  /from-config  ");

        Assert.Equal("/from-config", location.Path);
        Assert.Equal(ServerDataDirectorySource.Configuration, location.Source);
    }

    [Fact]
    public void Resolve_BlankOverrides_FallBackToTheDefault()
    {
        var location = ServerDataDirectory.Resolve("", "   ");

        Assert.Equal(ServerDataDirectorySource.Default, location.Source);
    }

    [Fact]
    public void Resolve_Default_IsOutsideTheBuildOutput()
    {
        var location = ServerDataDirectory.Resolve(null, null);

        Assert.StartsWith(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), location.Path);
        Assert.DoesNotContain(AppContext.BaseDirectory, location.Path);
    }

    [Fact]
    public void Resolve_Default_IsNotInsideTheClientsInstanceNamespace()
    {
        var clientRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EveUtils");

        var location = ServerDataDirectory.Resolve(null, null);

        Assert.False(location.Path.StartsWith(clientRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }

    [Fact]
    public void OrderForMove_TokenProtectorKey_MovesLast()
    {
        string[] ordered = [.. ServerDataDirectory.OrderForMove(
            ["token-protector.key", "eve-utils-server.db", "server-cert.pfx"])];

        Assert.Equal("token-protector.key", ordered[^1]);
    }

    [Fact]
    public void MigrateLegacyContents_DefaultSource_MovesEverythingAndEmptiesTheLegacyFiles()
    {
        _SeedLegacyInstallation();

        var migration = ServerDataDirectory.MigrateLegacyContents(
            new ServerDataLocation(_target, ServerDataDirectorySource.Default), _legacy);

        Assert.Contains("eve-utils-server.db", migration.Moved);
        Assert.Contains("token-protector.key", migration.Moved);
        Assert.Contains("sde", migration.Moved);
        Assert.Empty(migration.LeftBehind);
        Assert.True(File.Exists(Path.Combine(_target, "token-protector.key")));
        Assert.True(File.Exists(Path.Combine(_target, "sde", "sde.sqlite")));
        Assert.Empty(Directory.GetFileSystemEntries(_legacy));
    }

    [Fact]
    public void MigrateLegacyContents_NoSqliteDatabase_MovesTheRest()
    {
        // A server on MySQL/SqlServer/PostgreSql keeps no eve-utils-server.db, so the move works off what is
        // actually there rather than off a fixed list of six.
        _SeedLegacyInstallation();
        File.Delete(Path.Combine(_legacy, "eve-utils-server.db"));

        var migration = ServerDataDirectory.MigrateLegacyContents(
            new ServerDataLocation(_target, ServerDataDirectorySource.Default), _legacy);

        Assert.DoesNotContain("eve-utils-server.db", migration.Moved);
        Assert.Contains("token-protector.key", migration.Moved);
        Assert.Contains("server-cert.pfx", migration.Moved);
        Assert.Empty(Directory.GetFileSystemEntries(_legacy));
    }

    [Fact]
    public void MigrateLegacyContents_EnvironmentVariableSource_LeavesTheInstallationAlone()
    {
        _SeedLegacyInstallation();

        var migration = ServerDataDirectory.MigrateLegacyContents(
            new ServerDataLocation(_target, ServerDataDirectorySource.EnvironmentVariable), _legacy);

        Assert.False(migration.Ran);
        Assert.True(File.Exists(Path.Combine(_legacy, "token-protector.key")));
        Assert.False(Directory.Exists(_target));
    }

    [Fact]
    public void MigrateLegacyContents_ConfiguredSource_LeavesTheInstallationAlone()
    {
        _SeedLegacyInstallation();

        var migration = ServerDataDirectory.MigrateLegacyContents(
            new ServerDataLocation(_target, ServerDataDirectorySource.Configuration), _legacy);

        Assert.False(migration.Ran);
        Assert.True(File.Exists(Path.Combine(_legacy, "token-protector.key")));
    }

    [Fact]
    public void MigrateLegacyContents_TargetNotEmpty_DoesNotMergeTwoInstallations()
    {
        _SeedLegacyInstallation();
        Directory.CreateDirectory(_target);
        File.WriteAllText(Path.Combine(_target, "token-protector.key"), "target-key");

        var migration = ServerDataDirectory.MigrateLegacyContents(
            new ServerDataLocation(_target, ServerDataDirectorySource.Default), _legacy);

        Assert.False(migration.Ran);
        Assert.Equal("target-key", File.ReadAllText(Path.Combine(_target, "token-protector.key")));
        Assert.Equal("legacy-key", File.ReadAllText(Path.Combine(_legacy, "token-protector.key")));
    }

    [Fact]
    public void MigrateLegacyContents_NoLegacyDirectory_DoesNothing()
    {
        var migration = ServerDataDirectory.MigrateLegacyContents(
            new ServerDataLocation(_target, ServerDataDirectorySource.Default), _legacy);

        Assert.False(migration.Ran);
        Assert.False(Directory.Exists(_target));
    }

    private void _SeedLegacyInstallation()
    {
        Directory.CreateDirectory(_legacy);
        File.WriteAllText(Path.Combine(_legacy, "eve-utils-server.db"), "legacy-db");
        File.WriteAllText(Path.Combine(_legacy, "token-protector.key"), "legacy-key");
        File.WriteAllText(Path.Combine(_legacy, "server-cert.pfx"), "legacy-cert");
        File.WriteAllText(Path.Combine(_legacy, "app-errors.jsonl"), "{}");
        Directory.CreateDirectory(Path.Combine(_legacy, "esi-cache"));
        Directory.CreateDirectory(Path.Combine(_legacy, "sde"));
        File.WriteAllText(Path.Combine(_legacy, "sde", "sde.sqlite"), "legacy-sde");
    }
}
