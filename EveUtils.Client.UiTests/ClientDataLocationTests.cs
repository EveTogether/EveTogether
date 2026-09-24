using System.Security.Cryptography;
using EveUtils.Client.Composition;
using EveUtils.Shared.Modules.Esi;
using EveUtils.Shared.Modules.Settings.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

public sealed class ClientDataLocationTests : IDisposable
{
    private const int CharacterId = 90250177;
    private static readonly EsiTokenSet Tokens = new("access-token", "refresh-token", new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero));

    private readonly string _localAppData = Path.Combine(Path.GetTempPath(), "evetogether-datalocation-" + Guid.NewGuid().ToString("N"));

    private string Legacy => Path.Combine(_localAppData, ClientDataLocation.LegacyFolderName);
    private string Current => Path.Combine(_localAppData, ClientDataLocation.FolderName);

    public void Dispose()
    {
        if (Directory.Exists(_localAppData))
            Directory.Delete(_localAppData, recursive: true);
    }

    [Fact]
    public void Resolve_OnlyTheLegacyFolderExists_MovesItWholeAndUsesTheNewOne()
    {
        WriteLegacyFolder();
        var before = Snapshot(Legacy);

        var migration = ClientDataLocation.Resolve(Legacy, Current);

        Assert.Equal(DataMigrationOutcome.Moved, migration.Outcome);
        Assert.Equal(Current, migration.Root);
        Assert.False(Directory.Exists(Legacy));
        Assert.Equal(before, Snapshot(Current));
    }

    [Fact]
    public async Task Resolve_AfterTheMove_TheSignInsOfEveryCharacterStillDecrypt()
    {
        Directory.CreateDirectory(Legacy);
        await new EncryptedPerCharacterTokenStore(Legacy).SaveAsync(CharacterId, Tokens, TestContext.Current.CancellationToken);

        var migration = ClientDataLocation.Resolve(Legacy, Current);

        Assert.Equal(DataMigrationOutcome.Moved, migration.Outcome);
        Assert.Equal(Tokens, await new EncryptedPerCharacterTokenStore(Current).LoadAsync(CharacterId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Resolve_AfterTheMove_TheClientOpensItsSettingsAsTheyWere()
    {
        var expected = new Dictionary<string, string>
        {
            ["ui.week-start"] = "Sunday",
            ["localapi.port"] = "7777",
            ["shortcut.save-run.global"] = "true",
            ["ui.main-window"] = "{\"Height\":700}"
        };
        var previousRoot = ClientDataLocation.RootOverride;

        try
        {
            ClientDataLocation.RootOverride = Legacy;
            using (var instance = TestClientInstance.Create(instanceName: "kept"))
            {
                instance.KeepDataOnDispose = true;
                var settings = instance.Services.GetRequiredService<ISettingRepository>();
                foreach (var (key, value) in expected)
                    await settings.UpsertAsync(key, value, TestContext.Current.CancellationToken);
            }

            SqliteConnection.ClearAllPools();
            Assert.Equal(DataMigrationOutcome.Moved, ClientDataLocation.Resolve(Legacy, Current).Outcome);

            ClientDataLocation.RootOverride = Current;
            using var restarted = TestClientInstance.Create(instanceName: "kept");
            restarted.KeepDataOnDispose = true;
            var reopened = await restarted.Services.GetRequiredService<ISettingRepository>()
                .ListAsync(TestContext.Current.CancellationToken);

            Assert.Equal(expected, reopened.Where(setting => expected.ContainsKey(setting.Key)).ToDictionary(setting => setting.Key, setting => setting.Value));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            ClientDataLocation.RootOverride = previousRoot;
        }
    }

    [Fact]
    public void Resolve_TheNewFolderAlreadyExists_MovesAndOverwritesNothing()
    {
        WriteLegacyFolder();
        Directory.CreateDirectory(Current);
        File.WriteAllText(Path.Combine(Current, "client.db"), "newer");
        var legacyBefore = Snapshot(Legacy);
        var currentBefore = Snapshot(Current);

        var migration = ClientDataLocation.Resolve(Legacy, Current);

        Assert.Equal(DataMigrationOutcome.NotNeeded, migration.Outcome);
        Assert.Equal(Current, migration.Root);
        Assert.Equal(legacyBefore, Snapshot(Legacy));
        Assert.Equal(currentBefore, Snapshot(Current));
    }

    [Fact]
    public void Resolve_NeitherFolderExists_CreatesNothing()
    {
        Directory.CreateDirectory(_localAppData);

        var migration = ClientDataLocation.Resolve(Legacy, Current);

        Assert.Equal(DataMigrationOutcome.NotNeeded, migration.Outcome);
        Assert.Equal(Current, migration.Root);
        Assert.Empty(Directory.GetFileSystemEntries(_localAppData));
    }

    [Fact]
    public void Resolve_TheMoveFails_FallsBackToTheLegacyFolderUntouched()
    {
        WriteLegacyFolder();
        var before = Snapshot(Legacy);
        var unreachable = Path.Combine(_localAppData, "missing-parent", ClientDataLocation.FolderName);

        var migration = ClientDataLocation.Resolve(Legacy, unreachable);

        Assert.Equal(DataMigrationOutcome.Failed, migration.Outcome);
        Assert.Equal(Legacy, migration.Root);
        Assert.NotNull(migration.Error);
        Assert.Equal(before, Snapshot(Legacy));
        Assert.False(Directory.Exists(unreachable));
    }

    [Fact]
    public void Resolve_AnotherClientHoldsAFileOpen_FallsBackThenMovesAtTheNextStart()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows refuses to rename a folder that has a file open in it.");
        WriteLegacyFolder();
        var before = Snapshot(Legacy);

        using (new FileStream(Path.Combine(Legacy, "client.db"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var failed = ClientDataLocation.Resolve(Legacy, Current);

            Assert.Equal(DataMigrationOutcome.Failed, failed.Outcome);
            Assert.Equal(Legacy, failed.Root);
            Assert.False(Directory.Exists(Current));
        }

        var retried = ClientDataLocation.Resolve(Legacy, Current);

        Assert.Equal(DataMigrationOutcome.Moved, retried.Outcome);
        Assert.Equal(before, Snapshot(Current));
    }

    [Fact]
    public void InstanceName_OnlyTheOldVariableIsSet_StillCountsAsTheInstance()
    {
        using var variables = new InstanceVariables(newName: null, legacyName: "old-name");

        Assert.Equal("old-name", ClientDataLocation.InstanceName());
    }

    [Fact]
    public void InstanceName_BothVariablesAreSet_TheNewOneWins()
    {
        using var variables = new InstanceVariables(newName: "new-name", legacyName: "old-name");

        Assert.Equal("new-name", ClientDataLocation.InstanceName());
    }

    [Fact]
    public void InstanceName_NeitherVariableIsSet_IsNull()
    {
        using var variables = new InstanceVariables(newName: null, legacyName: null);

        Assert.Null(ClientDataLocation.InstanceName());
    }

    [Fact]
    public void TestClientInstance_Create_KeepsItsDataUnderTheTempFolder()
    {
        using var instance = TestClientInstance.Create();

        Assert.StartsWith(Path.GetTempPath(), instance.DataDirectory);
        Assert.StartsWith(TestDataRoot.Path, instance.DataDirectory);
    }

    private void WriteLegacyFolder()
    {
        WriteFile("client.db");
        WriteFile("client.db-wal");
        WriteFile("characters.json");
        WriteFile("esi-90250177.bin");
        WriteFile("esi-90250177.key");
        WriteFile(Path.Combine("esi-cache", "page.json"));
        WriteFile(Path.Combine("sde", "sde.sqlite"));
        WriteFile(Path.Combine("eve-settings-backups", "backup-1", "manifest.json"));
        WriteFile(Path.Combine("second-instance", "client.db"));
    }

    private void WriteFile(string relativePath)
    {
        var path = Path.Combine(Legacy, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, RandomNumberGenerator.GetBytes(64));
    }

    private static Dictionary<string, string> Snapshot(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToDictionary(
            file => Path.GetRelativePath(root, file),
            file => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))));

    private sealed class InstanceVariables : IDisposable
    {
        private readonly string? _previousNew = Environment.GetEnvironmentVariable(ClientDataLocation.InstanceVariable);
        private readonly string? _previousLegacy = Environment.GetEnvironmentVariable(ClientDataLocation.LegacyInstanceVariable);

        public InstanceVariables(string? newName, string? legacyName)
        {
            Environment.SetEnvironmentVariable(ClientDataLocation.InstanceVariable, newName);
            Environment.SetEnvironmentVariable(ClientDataLocation.LegacyInstanceVariable, legacyName);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(ClientDataLocation.InstanceVariable, _previousNew);
            Environment.SetEnvironmentVariable(ClientDataLocation.LegacyInstanceVariable, _previousLegacy);
        }
    }
}
