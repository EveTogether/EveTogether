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
    /// The account link: EVE writes a session's character and account file together on logout, so the account written
    /// beside a character is the one it is on. Several accounts equally close prove nothing — that character is left
    /// out rather than guessed at, because a wrong link is worse than none.
    /// </summary>
    [Fact]
    public void DeriveAccountLinks_LinksACharacterToTheAccountWrittenWithIt_AndSkipsAmbiguousOnes()
    {
        var profileDirectory = _NewProfile("settings_Default");
        var evening = new DateTime(2026, 8, 29, 22, 11, 0, DateTimeKind.Utc);
        var earlier = new DateTime(2026, 8, 29, 20, 5, 0, DateTimeKind.Utc);

        _WriteFile(profileDirectory, "core_char_90000001.dat", "alice", evening);
        _WriteFile(profileDirectory, "core_user_1001.dat", "account one", evening.AddSeconds(12));

        // Two clients closed together: three characters and two accounts share one moment.
        _WriteFile(profileDirectory, "core_char_90000002.dat", "bob", earlier);
        _WriteFile(profileDirectory, "core_char_90000003.dat", "carol", earlier);
        _WriteFile(profileDirectory, "core_user_1002.dat", "account two", earlier);
        _WriteFile(profileDirectory, "core_user_1003.dat", "account three", earlier);

        var links = EveSettingsLocator.DeriveAccountLinks([EveSettingsLocator.LoadProfile(profileDirectory)]);

        Assert.Equal([90000001L], links[1001]);
        Assert.False(links.ContainsKey(1002));
        Assert.False(links.ContainsKey(1003));
    }

    /// <summary>
    /// The case the operator actually has (ET-64): the profile he multiboxes in has six clients closed at once and
    /// says nothing, while a quieter profile beside it holds a clean one-by-one login — pairs on the same second,
    /// seconds between the pairs. Read together, every account is placed.
    /// </summary>
    [Fact]
    public void DeriveAccountLinks_ReadsTheTraceFromAnotherProfile_WhenTheOneInFrontOfYouSaysNothing()
    {
        // settings_Default: everything closed in the same second — nothing to conclude here.
        var multibox = _NewProfile("settings_Default");
        var together = new DateTime(2026, 8, 29, 23, 34, 0, DateTimeKind.Utc);
        foreach (var id in new[] { 90250177L, 2123169375L, 2122696898L })
            _WriteFile(multibox, $"core_char_{id}.dat", "x", together);
        foreach (var id in new[] { 7417348L, 31203498L, 30514680L })
            _WriteFile(multibox, $"core_user_{id}.dat", "x", together);

        // settings_minimal: logged in one after another, pairs on the same second, a few seconds apart.
        var minimal = _NewProfile("settings_minimal");
        var start = new DateTime(2025, 11, 18, 19, 0, 0, DateTimeKind.Utc);
        var pairs = new[] { (7417348L, 90250177L), (31203498L, 2123169375L), (30514680L, 2122696898L) };
        for (var index = 0; index < pairs.Length; index++)
        {
            var moment = start.AddSeconds(index * 5);
            _WriteFile(minimal, $"core_user_{pairs[index].Item1}.dat", "x", moment);
            _WriteFile(minimal, $"core_char_{pairs[index].Item2}.dat", "x", moment);
        }

        var links = EveSettingsLocator.DeriveAccountLinks(
            EveSettingsLocator.LoadProfiles(Path.Combine(_root, "install")));

        Assert.Equal([90250177L], links[7417348]);
        Assert.Equal([2123169375L], links[31203498]);
        Assert.Equal([2122696898L], links[30514680]);
    }

    /// <summary>An account can hold several characters, and two sessions on different evenings both count.</summary>
    [Fact]
    public void DeriveAccountLinks_PutsSeveralCharactersOnOneAccount()
    {
        var profileDirectory = _NewProfile("settings_Default");
        var monday = new DateTime(2026, 8, 24, 21, 0, 0, DateTimeKind.Utc);
        var tuesday = new DateTime(2026, 8, 25, 21, 0, 0, DateTimeKind.Utc);

        _WriteFile(profileDirectory, "core_user_1001.dat", "one", monday);
        _WriteFile(profileDirectory, "core_char_90000001.dat", "alice", monday);
        _WriteFile(profileDirectory, "core_char_90000002.dat", "bob", tuesday);

        var links = EveSettingsLocator.DeriveAccountLinks([EveSettingsLocator.LoadProfile(profileDirectory)]);

        // Only the character written in the same session counts; a day later is nobody's session.
        Assert.Equal([90000001L], links[1001]);

        File.SetLastWriteTimeUtc(Path.Combine(profileDirectory, "core_char_90000002.dat"), monday.AddSeconds(1));
        links = EveSettingsLocator.DeriveAccountLinks([EveSettingsLocator.LoadProfile(profileDirectory)]);
        Assert.Equal([90000001L, 90000002L], links[1001]);
    }

    /// <summary>
    /// What is remembered outranks what is inferred, and a link the user stated is never rewritten by a later guess —
    /// which is the whole reason for remembering: one evening of multiboxing must not undo what a quiet one proved.
    /// </summary>
    [Fact]
    public void Merge_KeepsWhatIsKnown_AndNeverLetsAGuessOverruleTheUser()
    {
        var now = new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
        var stored = new Dictionary<long, AccountCharacterLink>
        {
            [1001] = new() { AccountId = 1001, CharacterIds = [90000001L], Origin = AccountLinkOrigin.UserSet },
            [1002] = new() { AccountId = 1002, CharacterIds = [90000002L], Origin = AccountLinkOrigin.Derived }
        };

        var (merged, changed) = AccountLinkStore.Merge(stored, new Dictionary<long, IReadOnlyList<long>>
        {
            [1003] = [90000001L],              // contradicts what the user said: dropped
            [1002] = [90000002L, 90000003L],   // adds a second character to a derived link: kept
            [1004] = [90000004L]               // new: kept
        }, now);

        Assert.True(changed);
        Assert.Equal([90000001L], merged[1001].CharacterIds);
        Assert.Equal(AccountLinkOrigin.UserSet, merged[1001].Origin);
        Assert.Equal([90000002L, 90000003L], merged[1002].CharacterIds);
        Assert.False(merged.ContainsKey(1003));
        Assert.Equal([90000004L], merged[1004].CharacterIds);

        // Nothing new to say → nothing written back.
        Assert.False(AccountLinkStore.Merge(merged, new Dictionary<long, IReadOnlyList<long>>
        {
            [1002] = [90000002L]
        }, now).Changed);
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
