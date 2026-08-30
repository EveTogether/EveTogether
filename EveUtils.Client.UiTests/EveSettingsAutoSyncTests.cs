using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Client.EveSettings;
using EveUtils.Client.Fleet;
using EveUtils.Client.Platform;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// The automatic sync (ET-60): it runs only when every EVE client is closed and has stayed closed, it does nothing
/// when nothing changed, it always backs up first, it stops if a client starts mid-run, and it prunes only what it
/// made itself. Every test runs against a throwaway settings folder — never a real EVE installation, and never the
/// real backup folder.
/// </summary>
public sealed class EveSettingsAutoSyncTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "eveutils-autosync-" + Guid.NewGuid().ToString("N"));

    private sealed class OfflineEsiSource : IExternalCharacterEsiSource
    {
        public Task<ExternalCharacterInfo> FetchAsync(int characterId, CancellationToken cancellationToken = default) =>
            Task.FromResult(ExternalCharacterInfo.Unknown(characterId));
    }

    private sealed class FakeClientProbe : IEveClientProbe
    {
        public int Processes { get; set; }
        public EveClientEvidence Evidence { get; set; } = EveClientEvidence.Empty;
        public EveClientEvidence Probe() => Evidence;
        public int RunningClientCount() => Processes;
        public bool Activate(string characterName) => false;
    }

    /// <summary>The running test's cancellation token, so a hung call fails the test instead of the run.</summary>
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string InstallRoot => Path.Combine(_root, "install");

    private string _WriteProfile(string name, (long Id, string Content)[] characters, (long Id, string Content)[] accounts)
    {
        var directory = Path.Combine(InstallRoot, name);
        Directory.CreateDirectory(directory);
        foreach (var (id, content) in characters)
            File.WriteAllText(Path.Combine(directory, $"core_char_{id}.dat"), content, Encoding.UTF8);
        foreach (var (id, content) in accounts)
            File.WriteAllText(Path.Combine(directory, $"core_user_{id}.dat"), content, Encoding.UTF8);
        return directory;
    }

    private static TestClientInstance _NewInstance(FakeClientProbe probe) =>
        TestClientInstance.Create(services =>
        {
            services.AddSingleton<IExternalCharacterEsiSource, OfflineEsiSource>();
            services.AddSingleton<IEveClientProbe>(probe);
        });

    // The clock the service reads, so "the clients have been closed long enough" is a decision a test can make
    // rather than a wait it has to sit through.
    private DateTimeOffset _now = new(2026, 8, 30, 20, 0, 0, TimeSpan.Zero);

    private AutoSettingsSyncService _NewService(TestClientInstance instance) => new(
        instance.Services.GetRequiredService<SettingsSyncService>(),
        instance.Services.GetRequiredService<SettingsBackupService>(),
        instance.Services.GetRequiredService<EveSettingsNameResolver>(),
        instance.Services.GetRequiredService<EveSettingsPreferences>(),
        instance.Services.GetRequiredService<EveClientPresenceService>(),
        now: () => _now);

    private Task _ConfigureAsync(TestClientInstance instance, AutoSyncSettings settings) =>
        instance.Services.GetRequiredService<EveSettingsPreferences>().SaveAutoSyncAsync(settings);

    private AutoSyncSettings _Rule(bool enabled = true, long source = 90000001, params long[] targets) => new()
    {
        Enabled = enabled,
        InstallRoot = InstallRoot,
        ProfileName = "settings_Default",
        CharacterSourceId = source,
        CharacterTargetIds = targets.Length == 0 ? [90000002L] : targets
    };

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Scratch files.
        }
    }

    /// <summary>Nothing configured, or configured and switched off: the automaton does not so much as look at the
    /// files. Off is the default and has to stay harmless.</summary>
    [Fact]
    public async Task DoesNothing_WhenItIsOffOrNothingIsSetUp()
    {
        var profileDirectory = _WriteProfile("settings_Default", [(90000001, "source"), (90000002, "target")], []);
        var probe = new FakeClientProbe();
        using var instance = _NewInstance(probe);
        var service = _NewService(instance);
        _now = _now.AddMinutes(5);

        Assert.Equal(AutoSyncPass.Idle, await service.RunOnceAsync(Ct));

        await _ConfigureAsync(instance, _Rule(enabled: false));
        Assert.Equal(AutoSyncPass.Idle, await service.RunOnceAsync(Ct));

        Assert.Equal("target", File.ReadAllText(Path.Combine(profileDirectory, "core_char_90000002.dat")));
        Assert.Empty(instance.Services.GetRequiredService<SettingsBackupService>().List());
    }

    /// <summary>
    /// The condition the whole feature rests on. A running client blocks it — including one parked on the login
    /// screen, which appears in no game log and still rewrites its files on exit — and after the last one closes it
    /// waits out the settle window, because EVE writes its settings <em>while</em> shutting down.
    /// </summary>
    [Fact]
    public async Task WaitsForEveryClientToClose_AndThenForEveToFinishWriting()
    {
        var profileDirectory = _WriteProfile("settings_Default", [(90000001, "source"), (90000002, "target")], []);
        var probe = new FakeClientProbe { Processes = 1 };   // running, nobody logged in
        using var instance = _NewInstance(probe);
        await _ConfigureAsync(instance, _Rule());
        var service = _NewService(instance);

        _now = _now.AddMinutes(10);
        Assert.Equal(AutoSyncPass.ClientsRunning, await service.RunOnceAsync(Ct));
        Assert.Equal("target", File.ReadAllText(Path.Combine(profileDirectory, "core_char_90000002.dat")));

        probe.Processes = 0;                       // the last client just closed
        _now = _now.AddSeconds(15);                // less than the settle window
        Assert.Equal(AutoSyncPass.Settling, await service.RunOnceAsync(Ct));
        Assert.Equal("target", File.ReadAllText(Path.Combine(profileDirectory, "core_char_90000002.dat")));

        _now = _now.AddSeconds(30);
        Assert.Equal(AutoSyncPass.Synced, await service.RunOnceAsync(Ct));
        Assert.Equal("source", File.ReadAllText(Path.Combine(profileDirectory, "core_char_90000002.dat")));
    }

    /// <summary>
    /// Switching it on right after quitting EVE must still wait: the settle window starts when the last client
    /// closed, not when the feature was enabled. Otherwise the very first run — the one nobody is watching — reads
    /// files EVE may still be writing.
    /// </summary>
    [Fact]
    public async Task StillWaitsOutTheSettleWindow_WhenItIsSwitchedOnJustAfterAClientClosed()
    {
        var profileDirectory = _WriteProfile("settings_Default", [(90000001, "source"), (90000002, "target")], []);
        var probe = new FakeClientProbe { Processes = 1 };
        using var instance = _NewInstance(probe);
        var service = _NewService(instance);

        _now = _now.AddMinutes(30);                                  // EVE has been open a while, this was off
        Assert.Equal(AutoSyncPass.Idle, await service.RunOnceAsync(Ct));

        probe.Processes = 0;                                         // the client closes …
        await _ConfigureAsync(instance, _Rule());                    // … and only now is this switched on
        _now = _now.AddSeconds(5);

        Assert.Equal(AutoSyncPass.Settling, await service.RunOnceAsync(Ct));
        Assert.Equal("target", File.ReadAllText(Path.Combine(profileDirectory, "core_char_90000002.dat")));

        _now = _now.AddSeconds(40);
        Assert.Equal(AutoSyncPass.Synced, await service.RunOnceAsync(Ct));
    }

    /// <summary>The backup is not optional because nobody pressed anything: an automatic run leaves the same full
    /// snapshot behind, marked as its own kind so it can be told apart and pruned.</summary>
    [Fact]
    public async Task BacksTheWholeProfileUpFirst_AndSaysWhatItOverwrote()
    {
        _WriteProfile("settings_Default", [(90000001, "source"), (90000002, "target")], [(1001, "account")]);
        using var instance = _NewInstance(new FakeClientProbe());
        await instance.Services.GetRequiredService<Shared.Identity.ICharacterRegistry>()
            .AddOrUpdateAsync(new Shared.Identity.Character("Jithran", 90000001), Ct);
        await _ConfigureAsync(instance, _Rule());
        var service = _NewService(instance);
        _now = _now.AddMinutes(1);

        Assert.Equal(AutoSyncPass.Synced, await service.RunOnceAsync(Ct));

        var backup = Assert.Single(instance.Services.GetRequiredService<SettingsBackupService>().List());
        Assert.Equal(BackupReason.BeforeAutoSync, backup.Manifest.Reason);
        Assert.Equal("2 characters and 1 account", backup.Manifest.ContentsSummary);   // the whole profile
        Assert.Equal("target", File.ReadAllText(Path.Combine(backup.FilesDirectory, "core_char_90000002.dat")));
        // What it overwrote, in names, on the backup and in the history.
        Assert.Contains("Jithran", backup.Manifest.Note);

        var history = await instance.Services.GetRequiredService<EveSettingsPreferences>().LoadAutoSyncHistoryAsync(Ct);
        Assert.Contains("Jithran", history[0].Summary);
        Assert.Equal(backup.Id, history[0].BackupId);
    }

    /// <summary>
    /// Nothing changed, nothing done — not even a backup. A timer that copied regardless would bury the useful
    /// snapshots under identical ones within a day.
    /// </summary>
    [Fact]
    public async Task DoesNothingAtAll_WhenTheFilesAlreadyMatch()
    {
        var profileDirectory = _WriteProfile("settings_Default", [(90000001, "source"), (90000002, "target")], []);
        using var instance = _NewInstance(new FakeClientProbe());
        await _ConfigureAsync(instance, _Rule());
        var service = _NewService(instance);
        var backups = instance.Services.GetRequiredService<SettingsBackupService>();
        _now = _now.AddMinutes(1);

        Assert.Equal(AutoSyncPass.Synced, await service.RunOnceAsync(Ct));
        Assert.Single(backups.List());

        _now = _now.AddMinutes(1);
        Assert.Equal(AutoSyncPass.UpToDate, await service.RunOnceAsync(Ct));
        Assert.Single(backups.List());   // no second snapshot of a profile nothing happened to

        // Play on the source again and it picks straight back up.
        File.WriteAllText(Path.Combine(profileDirectory, "core_char_90000001.dat"), "changed", Encoding.UTF8);
        _now = _now.AddMinutes(1);
        Assert.Equal(AutoSyncPass.Synced, await service.RunOnceAsync(Ct));
        Assert.Equal("changed", File.ReadAllText(Path.Combine(profileDirectory, "core_char_90000002.dat")));
        Assert.Equal(2, backups.List().Count);
    }

    /// <summary>A client starting mid-run stops it: whatever EVE has open would rewrite the file at logout anyway,
    /// so finishing would be worse than stopping and saying so.</summary>
    [Fact]
    public async Task StopsPartWay_WhenAClientStartsWhileItIsCopying()
    {
        var profileDirectory = _WriteProfile("settings_Default",
            [(90000001, "source"), (90000002, "a"), (90000003, "b")], []);

        // The probe says "all clear" for the opening check, the pre-backup check and the first file, then reports a
        // client — one appearing between the check and the next write, which is the case that must not be finished.
        var asked = 0;
        using var stopping = TestClientInstance.Create(services =>
        {
            services.AddSingleton<IExternalCharacterEsiSource, OfflineEsiSource>();
            services.AddSingleton<IEveClientProbe>(new CountingProbe(() => ++asked > 3));
        });
        await stopping.Services.GetRequiredService<EveSettingsPreferences>()
            .SaveAutoSyncAsync(_Rule(targets: [90000002L, 90000003L]), Ct);
        var service = _NewService(stopping);
        _now = _now.AddMinutes(1);

        Assert.Equal(AutoSyncPass.Failed, await service.RunOnceAsync(Ct));   // "did not finish" is not a success

        var history = await stopping.Services.GetRequiredService<EveSettingsPreferences>().LoadAutoSyncHistoryAsync(Ct);
        Assert.Contains("client started", history[0].Summary);
        Assert.Equal("source", File.ReadAllText(Path.Combine(profileDirectory, "core_char_90000002.dat")));
        Assert.Equal("b", File.ReadAllText(Path.Combine(profileDirectory, "core_char_90000003.dat")));   // not touched

        // The snapshot was taken before any of it, so even the half-done state is undoable.
        var backup = Assert.Single(stopping.Services.GetRequiredService<SettingsBackupService>().List());
        Assert.Equal("a", File.ReadAllText(Path.Combine(backup.FilesDirectory, "core_char_90000002.dat")));
    }

    /// <summary>Answers "no client" a fixed number of times and then "one is running".</summary>
    private sealed class CountingProbe(Func<bool> runningNow) : IEveClientProbe
    {
        public EveClientEvidence Probe() => EveClientEvidence.Empty;
        public int RunningClientCount() => runningNow() ? 1 : 0;
        public bool Activate(string characterName) => false;
    }

    /// <summary>
    /// Retention: an automaton that snapshots on every run fills a disk. It keeps the newest of its own and deletes
    /// the rest — and never touches a backup the user made by hand or one taken before a copy they pressed.
    /// </summary>
    [Fact]
    public void Prune_RemovesOnlyItsOwnOldSnapshots()
    {
        var profileDirectory = _WriteProfile("settings_Default", [(90000001, "a")], []);
        var backups = new SettingsBackupService(Path.Combine(_root, "et-data"));
        var profile = EveSettingsLocator.LoadProfile(profileDirectory);
        var names = new Dictionary<long, string>();

        for (var index = 0; index < 4; index++)
            Assert.True(backups.Create(profile, InstallRoot, names, BackupReason.BeforeAutoSync, $"auto {index}").IsSuccess);
        Assert.True(backups.Create(profile, InstallRoot, names, BackupReason.Manual, "by hand").IsSuccess);
        Assert.True(backups.Create(profile, InstallRoot, names, BackupReason.BeforeSync, "pressed").IsSuccess);

        var deleted = backups.Prune(2, BackupReason.BeforeAutoSync);

        Assert.Equal(2, deleted.Count);
        var left = backups.List();
        Assert.Equal(2, left.Count(backup => backup.Manifest.Reason == BackupReason.BeforeAutoSync));
        Assert.Single(left, backup => backup.Manifest.Reason == BackupReason.Manual);
        Assert.Single(left, backup => backup.Manifest.Reason == BackupReason.BeforeSync);

        // However it is configured, the last known-good one of its own kind stays.
        backups.Prune(0, BackupReason.BeforeAutoSync);
        Assert.Single(backups.List(), backup => backup.Manifest.Reason == BackupReason.BeforeAutoSync);
    }

    /// <summary>A source that has vanished from the profile is reported once and then left alone — a fifteen-second
    /// loop writing the same complaint would push every real run out of the history.</summary>
    [Fact]
    public async Task ReportsAMissingSourceOnce_RatherThanEveryFifteenSeconds()
    {
        _WriteProfile("settings_Default", [(90000002, "target")], []);   // the configured source is not there
        using var instance = _NewInstance(new FakeClientProbe());
        await _ConfigureAsync(instance, _Rule());
        var service = _NewService(instance);
        _now = _now.AddMinutes(1);

        Assert.Equal(AutoSyncPass.Failed, await service.RunOnceAsync(Ct));
        _now = _now.AddMinutes(1);
        Assert.Equal(AutoSyncPass.Failed, await service.RunOnceAsync(Ct));

        var history = await instance.Services.GetRequiredService<EveSettingsPreferences>().LoadAutoSyncHistoryAsync(Ct);
        Assert.Single(history);
        Assert.Contains("no longer in settings_Default", history[0].Summary);
    }

    /// <summary>Character and account rules run together behind one backup, not one snapshot each — the second would
    /// be a picture of a profile the first had already changed.</summary>
    [Fact]
    public async Task CopiesCharactersAndAccountsInOneRun_BehindOneBackup()
    {
        var profileDirectory = _WriteProfile("settings_Default",
            [(90000001, "char-source"), (90000002, "char-target")],
            [(1001, "account-source"), (1002, "account-target")]);
        using var instance = _NewInstance(new FakeClientProbe());
        await _ConfigureAsync(instance, new AutoSyncSettings
        {
            Enabled = true,
            InstallRoot = InstallRoot,
            ProfileName = "settings_Default",
            CharacterSourceId = 90000001,
            CharacterTargetIds = [90000002L],
            AccountSourceId = 1001,
            AccountTargetIds = [1002L]
        });
        var service = _NewService(instance);
        _now = _now.AddMinutes(1);

        Assert.Equal(AutoSyncPass.Synced, await service.RunOnceAsync(Ct));

        Assert.Equal("char-source", File.ReadAllText(Path.Combine(profileDirectory, "core_char_90000002.dat")));
        Assert.Equal("account-source", File.ReadAllText(Path.Combine(profileDirectory, "core_user_1002.dat")));
        Assert.Single(instance.Services.GetRequiredService<SettingsBackupService>().List());
    }
}
