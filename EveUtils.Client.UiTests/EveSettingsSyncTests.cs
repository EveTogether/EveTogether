using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using EveUtils.Client.EveSettings;
using EveUtils.Shared.Messaging;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// The file half of EVE Settings Sync (ET-59): reading a profile, the two rules that keep a sync safe (never mix
/// character and account files, never rename a target), the mandatory backup and putting one back. Everything runs
/// against a throwaway directory of fake .dat files — never a real EVE installation.
/// </summary>
public sealed class EveSettingsSyncTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "eveutils-settings-" + Guid.NewGuid().ToString("N"));

    private string _NewProfile(string name)
    {
        var directory = Path.Combine(_root, "install", name);
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string _WriteFile(string profileDirectory, string fileName, string content, DateTime? written = null)
    {
        var path = Path.Combine(profileDirectory, fileName);
        File.WriteAllText(path, content, Encoding.UTF8);
        if (written is { } moment)
            File.SetLastWriteTimeUtc(path, moment);
        return path;
    }

    private static IReadOnlyDictionary<long, string> Names(params (long Id, string Name)[] names) =>
        names.ToDictionary(pair => pair.Id, pair => pair.Name);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Scratch files; a leftover temp directory is harmless.
        }
    }

    // ── Reading a profile ────────────────────────────────────────────────────────────────────────

    /// <summary>A real profile folder also holds stubs EVE leaves behind (core_char__.dat and worse). They carry no
    /// character, so they must never turn up as a source or a target.</summary>
    [Fact]
    public void LoadProfile_TakesOnlyRealCharacterAndAccountFiles()
    {
        var profileDirectory = _NewProfile("settings_Default");
        _WriteFile(profileDirectory, "core_char_90000001.dat", "alice");
        _WriteFile(profileDirectory, "core_char_90000002.dat", "bob");
        _WriteFile(profileDirectory, "core_user_1001.dat", "account one");
        _WriteFile(profileDirectory, "core_char__.dat", "stub");
        _WriteFile(profileDirectory, "core_user__.dat", "stub");
        _WriteFile(profileDirectory, "core_char_('char', None, 'dat').dat", "stub");
        _WriteFile(profileDirectory, "prefs.ini", "unrelated");

        var profile = EveSettingsLocator.LoadProfile(profileDirectory);

        Assert.Equal([90000001L, 90000002L], profile.Characters.Select(file => file.Id));
        Assert.Equal([1001L], profile.Accounts.Select(file => file.Id));
        Assert.All(profile.Characters, file => Assert.Equal(SettingsFileKind.Character, file.Kind));
    }

    [Fact]
    public void LoadProfiles_ListsEverySettingsFolderByName()
    {
        _WriteFile(_NewProfile("settings_Default"), "core_char_90000001.dat", "a");
        _WriteFile(_NewProfile("settings_minimal"), "core_char_90000001.dat", "b");
        Directory.CreateDirectory(Path.Combine(_root, "install", "cache"));   // not a profile

        var profiles = EveSettingsLocator.LoadProfiles(Path.Combine(_root, "install"));

        Assert.Equal(["settings_Default", "settings_minimal"], profiles.Select(profile => profile.Name));
    }

    /// <summary>
    /// The account hint: EVE writes a session's character and account file together on logout, so a lone account in
    /// a write window belongs to the characters written with it. Two accounts in one window prove nothing — that
    /// bucket is dropped rather than guessed at, because a wrong hint is worse than none.
    /// </summary>
    [Fact]
    public void AccountCharacterHints_LinkALoneAccountToItsSession_AndSkipAmbiguousOnes()
    {
        var profileDirectory = _NewProfile("settings_Default");
        var evening = new DateTime(2026, 8, 29, 22, 11, 0, DateTimeKind.Utc);
        var earlier = new DateTime(2026, 8, 29, 20, 5, 0, DateTimeKind.Utc);

        _WriteFile(profileDirectory, "core_char_90000001.dat", "alice", evening);
        _WriteFile(profileDirectory, "core_user_1001.dat", "account one", evening.AddSeconds(12));

        // Two clients closed together: three characters and two accounts share one window.
        _WriteFile(profileDirectory, "core_char_90000002.dat", "bob", earlier);
        _WriteFile(profileDirectory, "core_char_90000003.dat", "carol", earlier);
        _WriteFile(profileDirectory, "core_user_1002.dat", "account two", earlier);
        _WriteFile(profileDirectory, "core_user_1003.dat", "account three", earlier);

        var hints = EveSettingsLocator.AccountCharacterHints(EveSettingsLocator.LoadProfile(profileDirectory));

        Assert.Equal([90000001L], hints[1001]);
        Assert.False(hints.ContainsKey(1002));
        Assert.False(hints.ContainsKey(1003));
    }

    // ── Syncing ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The rule the whole feature rests on: a target keeps its own file name and id, only its contents change. Copy
    /// the source file <em>as</em> the target's name and EVE loads one pilot's settings under another's identity.
    /// </summary>
    [Fact]
    public void Apply_OverwritesTargetContents_ButNeverTheirNames()
    {
        var profileDirectory = _NewProfile("settings_Default");
        _WriteFile(profileDirectory, "core_char_90000001.dat", "alice-layout");
        _WriteFile(profileDirectory, "core_char_90000002.dat", "bob-layout");
        _WriteFile(profileDirectory, "core_char_90000003.dat", "carol-layout");

        var (sync, _) = _Services();
        var profile = EveSettingsLocator.LoadProfile(profileDirectory);
        var plan = new SettingsSyncPlan(profile, Path.Combine(_root, "install"),
            profile.Characters[0], "Alice",
            [profile.Characters[1], profile.Characters[2]], ["Bob", "Carol"]);

        var outcome = sync.Apply(plan, Names((90000001, "Alice"), (90000002, "Bob"), (90000003, "Carol")));

        Assert.True(outcome.IsSuccess);
        Assert.Equal(["Bob", "Carol"], outcome.Value!.Copied);
        Assert.Equal("alice-layout", File.ReadAllText(Path.Combine(profileDirectory, "core_char_90000002.dat")));
        Assert.Equal("alice-layout", File.ReadAllText(Path.Combine(profileDirectory, "core_char_90000003.dat")));
        Assert.Equal("alice-layout", File.ReadAllText(Path.Combine(profileDirectory, "core_char_90000001.dat")));
        Assert.Equal(3, Directory.GetFiles(profileDirectory, "core_char_*.dat").Length);   // no file renamed or added
    }

    /// <summary>Copying a character file onto an account file has to be impossible, not merely discouraged: the
    /// service refuses the plan even when a caller hands it one.</summary>
    [Fact]
    public void Apply_RefusesToMixCharacterAndAccountFiles()
    {
        var profileDirectory = _NewProfile("settings_Default");
        _WriteFile(profileDirectory, "core_char_90000001.dat", "alice-layout");
        _WriteFile(profileDirectory, "core_user_1001.dat", "account-one");

        var (sync, _) = _Services();
        var profile = EveSettingsLocator.LoadProfile(profileDirectory);
        var plan = new SettingsSyncPlan(profile, Path.Combine(_root, "install"),
            profile.Characters[0], "Alice", [profile.Accounts[0]], ["Account one"]);

        var outcome = sync.Apply(plan, Names());

        Assert.False(outcome.IsSuccess);
        Assert.Equal(MessageCodes.ValidationFailed, outcome.Messages[0].Code);
        Assert.Equal("account-one", File.ReadAllText(Path.Combine(profileDirectory, "core_user_1001.dat")));
    }

    [Fact]
    public void Apply_RefusesASourceThatIsAlsoATarget()
    {
        var profileDirectory = _NewProfile("settings_Default");
        _WriteFile(profileDirectory, "core_char_90000001.dat", "alice-layout");

        var (sync, _) = _Services();
        var profile = EveSettingsLocator.LoadProfile(profileDirectory);
        var plan = new SettingsSyncPlan(profile, Path.Combine(_root, "install"),
            profile.Characters[0], "Alice", [profile.Characters[0]], ["Alice"]);

        Assert.False(sync.Apply(plan, Names()).IsSuccess);
    }

    /// <summary>The backup is not a choice: a plain sync leaves a full, named snapshot of the profile behind.</summary>
    [Fact]
    public void Apply_AlwaysBacksUpTheWholeProfileFirst()
    {
        var profileDirectory = _NewProfile("settings_Default");
        _WriteFile(profileDirectory, "core_char_90000001.dat", "alice-layout");
        _WriteFile(profileDirectory, "core_char_90000002.dat", "bob-layout");
        _WriteFile(profileDirectory, "core_user_1001.dat", "account-one");

        var (sync, backups) = _Services();
        var profile = EveSettingsLocator.LoadProfile(profileDirectory);
        var plan = new SettingsSyncPlan(profile, Path.Combine(_root, "install"),
            profile.Characters[0], "Alice", [profile.Characters[1]], ["Bob"]);

        var outcome = sync.Apply(plan, Names((90000001, "Alice"), (90000002, "Bob"), (1001, "Main account")));
        Assert.True(outcome.IsSuccess);

        var backup = Assert.Single(backups.List());
        Assert.Equal(BackupReason.BeforeSync, backup.Manifest.Reason);
        Assert.Equal("settings_Default", backup.Manifest.ProfileName);
        Assert.Equal(2, backup.Manifest.CharacterCount);
        Assert.Equal(1, backup.Manifest.AccountCount);
        // The backup holds what the profile looked like BEFORE the copy, and says so in names (the ET-61 seam).
        Assert.Equal("bob-layout", File.ReadAllText(Path.Combine(backup.FilesDirectory, "core_char_90000002.dat")));
        Assert.Contains(backup.Manifest.Entries, entry => entry.Name == "Main account" && entry.Kind == SettingsFileKind.Account);
        Assert.Contains("Alice", backup.Manifest.Note);
    }

    // ── Backups ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>A backup you cannot put back is only a feeling. Restoring also snapshots the current state first,
    /// so the restore itself is undoable.</summary>
    [Fact]
    public void Restore_PutsTheFilesBack_AndSnapshotsWhatItReplaced()
    {
        var profileDirectory = _NewProfile("settings_Default");
        _WriteFile(profileDirectory, "core_char_90000001.dat", "original");
        _WriteFile(profileDirectory, "core_user_1001.dat", "original-account");

        var (_, backups) = _Services();
        var profile = EveSettingsLocator.LoadProfile(profileDirectory);
        var created = backups.Create(profile, Path.Combine(_root, "install"),
            Names((90000001, "Alice"), (1001, "Main account")), BackupReason.Manual, "by hand");
        Assert.True(created.IsSuccess);

        _WriteFile(profileDirectory, "core_char_90000001.dat", "ruined");

        var restored = backups.Restore(created.Value!, Names((90000001, "Alice")));

        Assert.True(restored.IsSuccess);
        Assert.Equal(2, restored.Value!.Restored.Count);
        Assert.Empty(restored.Value.Failed);
        Assert.Equal("original", File.ReadAllText(Path.Combine(profileDirectory, "core_char_90000001.dat")));

        var safety = backups.List().Single(backup => backup.Manifest.Reason == BackupReason.BeforeRestore);
        Assert.Equal("ruined", File.ReadAllText(Path.Combine(safety.FilesDirectory, "core_char_90000001.dat")));
        Assert.Equal(safety.DirectoryPath, restored.Value.SafetyBackupDirectory);
    }

    [Fact]
    public void Restore_RefusesWhenTheProfileItCameFromIsGone()
    {
        var profileDirectory = _NewProfile("settings_Default");
        _WriteFile(profileDirectory, "core_char_90000001.dat", "original");

        var (_, backups) = _Services();
        var created = backups.Create(EveSettingsLocator.LoadProfile(profileDirectory),
            Path.Combine(_root, "install"), Names((90000001, "Alice")), BackupReason.Manual, "by hand");
        Directory.Delete(profileDirectory, recursive: true);

        var restored = backups.Restore(created.Value!, Names());

        Assert.False(restored.IsSuccess);
        Assert.Equal(MessageCodes.NotFound, restored.Messages[0].Code);
    }

    [Fact]
    public void List_SkipsAFolderWithoutAReadableManifest_AndSortsNewestFirst()
    {
        var profileDirectory = _NewProfile("settings_Default");
        _WriteFile(profileDirectory, "core_char_90000001.dat", "one");

        var (_, backups) = _Services();
        var profile = EveSettingsLocator.LoadProfile(profileDirectory);
        var older = backups.Create(profile, "install", Names(), BackupReason.Manual, "first");
        var newer = backups.Create(profile, "install", Names(), BackupReason.BeforeSync, "second");
        Assert.True(older.IsSuccess && newer.IsSuccess);

        Directory.CreateDirectory(Path.Combine(backups.RootDirectory, "not-a-backup"));

        var listed = backups.List();
        Assert.Equal(2, listed.Count);
        Assert.Equal(BackupReason.BeforeSync, listed[0].Manifest.Reason);
    }

    [Fact]
    public void Delete_RemovesTheBackupFromDiskAndFromTheList()
    {
        var profileDirectory = _NewProfile("settings_Default");
        _WriteFile(profileDirectory, "core_char_90000001.dat", "one");

        var (_, backups) = _Services();
        var created = backups.Create(EveSettingsLocator.LoadProfile(profileDirectory), "install", Names(),
            BackupReason.Manual, "by hand");

        Assert.True(backups.Delete(created.Value!).IsSuccess);
        Assert.False(Directory.Exists(created.Value!.DirectoryPath));
        Assert.Empty(backups.List());
    }

    private (SettingsSyncService Sync, SettingsBackupService Backups) _Services()
    {
        var backups = new SettingsBackupService(Path.Combine(_root, "et-data"));
        return (new SettingsSyncService(backups), backups);
    }
}
