using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using EveUtils.Client.EveSettings;
using EveUtils.Shared.Messaging;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// Presets (ET-61): saving a chosen slice of a profile to one file, and reading it back on a machine that has
/// different folders — or none at all. Covers what a preset is allowed to contain, which is the part that matters
/// most, since this is a file the user hands to somebody else.
/// </summary>
public sealed class EveSettingsPresetTests : IDisposable
{
    // A path segment that could only have come from this machine — what a leak would look like in the manifest.
    private readonly string _root = Path.Combine(Path.GetTempPath(), "eveutils-preset-secretuser-" + Guid.NewGuid().ToString("N"));

    private string _NewProfile(string name)
    {
        var directory = Path.Combine(_root, "install", name);
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void _WriteFile(string profileDirectory, string fileName, string content) =>
        File.WriteAllText(Path.Combine(profileDirectory, fileName), content, Encoding.UTF8);

    private static IReadOnlyDictionary<long, string> Names(params (long Id, string Name)[] names) =>
        names.ToDictionary(pair => pair.Id, pair => pair.Name);

    private static EveSettingsNames NamesFor(params (long Id, string Name)[] names) =>
        new(names.ToDictionary(pair => pair.Id, pair => pair.Name),
            new Dictionary<long, string>(), new Dictionary<long, AccountCharacterLink>());

    private (SettingsPresetService Presets, SettingsBackupService Backups) _Services()
    {
        var backups = new SettingsBackupService(Path.Combine(_root, "et-data"));
        return (new SettingsPresetService(backups), backups);
    }

    private string _PresetPath => Path.Combine(_root, "carried", "default.etpreset");

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

    // ── What is in a preset, and what is not ─────────────────────────────────────────────────────

    /// <summary>
    /// The check the ticket asks for by name. A preset is a file the user passes around, so its contents are its
    /// own promise: the .dat files that were ticked and a description of them, and nothing else. In particular no
    /// absolute paths — the manifest's directory fields would otherwise spell out the Windows account name of
    /// whoever made it.
    /// </summary>
    [Fact]
    public void Export_HoldsTheTickedFilesAndTheirDescription_AndNoPathsOrAnythingElse()
    {
        var profileDirectory = _NewProfile("settings_Default");
        _WriteFile(profileDirectory, "core_char_90250177.dat", "jithran-layout");
        _WriteFile(profileDirectory, "core_char_90382598.dat", "not-in-the-preset");
        _WriteFile(profileDirectory, "core_user_7417348.dat", "jithran-account");

        var (presets, _) = _Services();
        var profile = EveSettingsLocator.LoadProfile(profileDirectory);
        var selection = new[] { profile.Characters.First(f => f.Id == 90250177), profile.Accounts[0] };

        var exported = presets.Export(_PresetPath, "default", PresetScope.Selection, profile, selection,
            Names((90250177, "Jithran"), (7417348, "Main account")));
        Assert.True(exported.IsSuccess);

        using var archive = ZipFile.OpenRead(_PresetPath);

        // Exactly the manifest and the two files that were ticked — nothing rode along.
        Assert.Equal(
            ["files/core_char_90250177.dat", "files/core_user_7417348.dat", "preset.json"],
            archive.Entries.Select(entry => entry.FullName).OrderBy(name => name, StringComparer.Ordinal));

        using var reader = new StreamReader(archive.GetEntry("preset.json")!.Open());
        var json = reader.ReadToEnd();

        // No paths: not the profile folder, not the install folder, not the scratch root that carries a user-like
        // segment, and no drive letter anywhere.
        Assert.DoesNotContain(profileDirectory, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_root, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secretuser", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(":\\", json, StringComparison.Ordinal);
        Assert.DoesNotContain(Environment.MachineName, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.UserName, json, StringComparison.OrdinalIgnoreCase);

        // What it does say: the names, the ids, the kinds, the profile it came from, when and with what.
        Assert.Contains("\"name\": \"default\"", json);
        Assert.Contains("Jithran", json);
        Assert.Contains("Main account", json);
        Assert.Contains("settings_Default", json);
        Assert.Contains("\"appVersion\"", json);
    }

    /// <summary>A preset is a subset on purpose — that is the point of it — and it says so when it is read back.</summary>
    [Fact]
    public void Export_KeepsASelectionASelection_AndTheWholeProfileWhole()
    {
        var profileDirectory = _NewProfile("settings_Default");
        _WriteFile(profileDirectory, "core_char_90250177.dat", "one");
        _WriteFile(profileDirectory, "core_char_90382598.dat", "two");
        _WriteFile(profileDirectory, "core_user_7417348.dat", "account");

        var (presets, _) = _Services();
        var profile = EveSettingsLocator.LoadProfile(profileDirectory);

        Assert.True(presets.Export(_PresetPath, "default", PresetScope.Selection, profile,
            [profile.Characters[0], profile.Accounts[0]], Names((90250177, "Jithran"))).IsSuccess);

        var read = presets.Read(_PresetPath);
        Assert.True(read.IsSuccess);
        Assert.Equal("default", read.Value!.Manifest.Name);
        Assert.Equal(PresetScope.Selection, read.Value.Manifest.Scope);
        Assert.Equal("1 character and 1 account", read.Value.Manifest.Contents.ContentsSummary);
        Assert.Contains("a selection from settings_Default", read.Value.Manifest.ScopeSummary);

        var whole = Path.Combine(_root, "carried", "everything.etpreset");
        Assert.True(presets.Export(whole, "everything", PresetScope.WholeProfile, profile,
            profile.Characters.Concat(profile.Accounts).ToList(), Names()).IsSuccess);
        Assert.Equal("2 characters and 1 account", presets.Read(whole).Value!.Manifest.Contents.ContentsSummary);
    }

    [Fact]
    public void Export_RefusesAnEmptySelectionOrANamelessPreset()
    {
        var profileDirectory = _NewProfile("settings_Default");
        _WriteFile(profileDirectory, "core_char_90250177.dat", "one");
        var (presets, _) = _Services();
        var profile = EveSettingsLocator.LoadProfile(profileDirectory);

        Assert.False(presets.Export(_PresetPath, "default", PresetScope.Selection, profile, [], Names()).IsSuccess);
        Assert.False(presets.Export(_PresetPath, "  ", PresetScope.Selection, profile, profile.Characters, Names()).IsSuccess);
        Assert.False(File.Exists(_PresetPath));
    }

    // ── Reading one back ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Read_RefusesSomethingThatIsNotAPreset()
    {
        Directory.CreateDirectory(Path.Combine(_root, "carried"));
        File.WriteAllText(_PresetPath, "not a zip at all");

        var (presets, _) = _Services();
        Assert.False(presets.Read(_PresetPath).IsSuccess);
    }

    /// <summary>A preset from a newer build is described in full and applied to nothing — the same rule a backup
    /// follows, and a preset comes from another machine by definition.</summary>
    [Fact]
    public void Import_RefusesAPresetFromANewerVersion_ButStillDescribesIt()
    {
        var profileDirectory = _NewProfile("settings_Default");
        _WriteFile(profileDirectory, "core_char_90250177.dat", "here");
        var (presets, backups) = _Services();
        var profile = EveSettingsLocator.LoadProfile(profileDirectory);

        _WriteRawPreset(_PresetPath, formatVersion: PresetManifest.CurrentFormatVersion + 1,
            entries: [("core_char_90250177.dat", SettingsFileKind.Character, 90250177, "Jithran")],
            files: [("core_char_90250177.dat", "from-the-future")]);

        var read = presets.Read(_PresetPath);
        Assert.True(read.IsSuccess);                       // still readable, still described
        Assert.Equal("Jithran", read.Value!.Manifest.Contents.Entries[0].Name);
        Assert.False(read.Value.CanApply);

        var plan = SettingsPresetService.BuildPlan(read.Value, profile, "install", NamesFor((90250177, "Jithran")));
        var imported = presets.Import(read.Value, plan, Names());

        Assert.False(imported.IsSuccess);
        Assert.Equal("here", File.ReadAllText(Path.Combine(profileDirectory, "core_char_90250177.dat")));
        Assert.Empty(backups.List());                      // refused before anything, backup included
    }

    /// <summary>A preset listing a file name EVE would never have written is refused at the door, before a single
    /// entry is unpacked.</summary>
    [Fact]
    public void Read_RefusesAPresetListingAFileEveWouldNotHaveWritten()
    {
        _WriteRawPreset(_PresetPath, PresetManifest.CurrentFormatVersion,
            entries: [("../../evil.dat", SettingsFileKind.Character, 90250177, "Jithran")],
            files: [("../../evil.dat", "payload")]);

        var (presets, _) = _Services();
        var read = presets.Read(_PresetPath);

        Assert.False(read.IsSuccess);
        Assert.Contains("would not have written", read.Messages[0].Text);
    }

    // ── Putting one down on the other machine ────────────────────────────────────────────────────

    /// <summary>
    /// The main use, on the machine it was carried to: EVE has just been installed, the profile folder is empty, and
    /// the preset's files land as new ones. That is the normal case here, not an edge case — and the backup taken
    /// first is empty rather than skipped, so even this is on the record.
    /// </summary>
    [Fact]
    public void Import_WritesNewFiles_OnAMachineWhereNothingExistsYet()
    {
        var source = _NewProfile("settings_Default");
        _WriteFile(source, "core_char_90250177.dat", "jithran-layout");
        _WriteFile(source, "core_user_7417348.dat", "jithran-account");

        var (presets, backups) = _Services();
        var sourceProfile = EveSettingsLocator.LoadProfile(source);
        Assert.True(presets.Export(_PresetPath, "default", PresetScope.Selection, sourceProfile,
            sourceProfile.Characters.Concat(sourceProfile.Accounts).ToList(),
            Names((90250177, "Jithran"), (7417348, "Main account"))).IsSuccess);

        // The other machine: EVE made the folder and nothing else.
        var fresh = Path.Combine(_root, "other-pc", "settings_Default");
        Directory.CreateDirectory(fresh);
        var freshProfile = EveSettingsLocator.LoadProfile(fresh);

        var preset = presets.Read(_PresetPath).Value!;
        var plan = SettingsPresetService.BuildPlan(preset, freshProfile, Path.Combine(_root, "other-pc"),
            EveSettingsNames.Empty);

        Assert.All(plan.Items, item => Assert.Equal(PresetImportAction.New, item.Action));

        var imported = presets.Import(preset, plan, Names());

        Assert.True(imported.IsSuccess);
        Assert.Equal(2, imported.Value!.Created.Count);
        Assert.Empty(imported.Value.Overwritten);
        Assert.Equal("jithran-layout", File.ReadAllText(Path.Combine(fresh, "core_char_90250177.dat")));
        Assert.Equal("jithran-account", File.ReadAllText(Path.Combine(fresh, "core_user_7417348.dat")));

        // A record exists even though there was nothing to keep.
        var backup = Assert.Single(backups.List());
        Assert.Equal(BackupReason.BeforeImport, backup.Manifest.Reason);
        Assert.Empty(backup.Manifest.Entries);
        Assert.Contains("default", backup.Manifest.Note);
    }

    /// <summary>Where the ids do line up the preview says "overwrite", and the profile is snapshotted first — the
    /// same rule as every other write in the tool.</summary>
    [Fact]
    public void Import_OverwritesMatchingIds_AfterBackingTheProfileUp()
    {
        var source = _NewProfile("settings_Default");
        _WriteFile(source, "core_char_90250177.dat", "new-layout");

        var target = Path.Combine(_root, "other-pc", "settings_Default");
        Directory.CreateDirectory(target);
        _WriteFile(target, "core_char_90250177.dat", "old-layout");
        _WriteFile(target, "core_char_90382598.dat", "untouched");

        var (presets, backups) = _Services();
        var sourceProfile = EveSettingsLocator.LoadProfile(source);
        Assert.True(presets.Export(_PresetPath, "default", PresetScope.Selection, sourceProfile,
            sourceProfile.Characters, Names((90250177, "Jithran"))).IsSuccess);

        var targetProfile = EveSettingsLocator.LoadProfile(target);
        var preset = presets.Read(_PresetPath).Value!;
        var plan = SettingsPresetService.BuildPlan(preset, targetProfile, Path.Combine(_root, "other-pc"),
            NamesFor((90250177, "Jithran"), (90382598, "Abnoba Auscent")));

        Assert.Equal(PresetImportAction.Overwrite, Assert.Single(plan.Items).Action);

        var imported = presets.Import(preset, plan, Names((90250177, "Jithran"), (90382598, "Abnoba Auscent")));

        Assert.True(imported.IsSuccess);
        Assert.Equal("new-layout", File.ReadAllText(Path.Combine(target, "core_char_90250177.dat")));
        Assert.Equal("untouched", File.ReadAllText(Path.Combine(target, "core_char_90382598.dat")));

        var backup = Assert.Single(backups.List());
        Assert.Equal("old-layout", File.ReadAllText(Path.Combine(backup.FilesDirectory, "core_char_90250177.dat")));
        Assert.Equal(2, backup.Manifest.Entries.Count);   // the whole profile, not only what the preset touched
    }

    /// <summary>
    /// The line can be pointed somewhere else — the answer to "what if the character in the preset is not the one I
    /// want it on here". The target keeps its own file name and id; only the bytes move. And a line set to skip is
    /// left alone.
    /// </summary>
    [Fact]
    public void Import_CanBePointedAtADifferentTarget_OrSkipped()
    {
        var source = _NewProfile("settings_Default");
        _WriteFile(source, "core_char_90250177.dat", "jithran-layout");
        _WriteFile(source, "core_user_7417348.dat", "jithran-account");

        var target = Path.Combine(_root, "other-pc", "settings_Default");
        Directory.CreateDirectory(target);
        _WriteFile(target, "core_char_2123169375.dat", "lyra-layout");
        _WriteFile(target, "core_user_31203498.dat", "other-account");

        var (presets, _) = _Services();
        var sourceProfile = EveSettingsLocator.LoadProfile(source);
        Assert.True(presets.Export(_PresetPath, "default", PresetScope.Selection, sourceProfile,
            sourceProfile.Characters.Concat(sourceProfile.Accounts).ToList(), Names((90250177, "Jithran"))).IsSuccess);

        var targetProfile = EveSettingsLocator.LoadProfile(target);
        var preset = presets.Read(_PresetPath).Value!;
        var lyra = targetProfile.Characters.Single();

        var items = SettingsPresetService.BuildPlan(preset, targetProfile, "install", EveSettingsNames.Empty).Items
            .Select(item => item.Entry.Kind == SettingsFileKind.Character
                ? item with { Action = PresetImportAction.Overwrite, Target = lyra, TargetFileName = lyra.FileName }
                : item with { Action = PresetImportAction.Skip })
            .ToList();

        var imported = presets.Import(preset, new PresetImportPlan(targetProfile, "install", items), Names());

        Assert.True(imported.IsSuccess);
        Assert.Equal("jithran-layout", File.ReadAllText(Path.Combine(target, "core_char_2123169375.dat")));
        Assert.Equal("other-account", File.ReadAllText(Path.Combine(target, "core_user_31203498.dat")));   // skipped
        Assert.Single(imported.Value!.Overwritten);
        Assert.Single(imported.Value.Skipped);
        Assert.False(File.Exists(Path.Combine(target, "core_char_90250177.dat")));   // no file renamed into being
    }

    /// <summary>The rule that holds the whole tool up survives the trip: a character's settings can never be written
    /// onto an account's file, however the plan was put together.</summary>
    [Fact]
    public void Import_RefusesToWriteACharacterOntoAnAccount()
    {
        var source = _NewProfile("settings_Default");
        _WriteFile(source, "core_char_90250177.dat", "jithran-layout");

        var target = Path.Combine(_root, "other-pc", "settings_Default");
        Directory.CreateDirectory(target);
        _WriteFile(target, "core_user_7417348.dat", "account");

        var (presets, backups) = _Services();
        var sourceProfile = EveSettingsLocator.LoadProfile(source);
        Assert.True(presets.Export(_PresetPath, "default", PresetScope.Selection, sourceProfile,
            sourceProfile.Characters, Names()).IsSuccess);

        var targetProfile = EveSettingsLocator.LoadProfile(target);
        var preset = presets.Read(_PresetPath).Value!;
        var account = targetProfile.Accounts.Single();
        var crossed = new PresetImportPlan(targetProfile, "install",
        [
            new PresetImportItem(preset.Manifest.Contents.Entries[0], PresetImportAction.Overwrite, account,
                account.FileName, "Main account")
        ]);

        var imported = presets.Import(preset, crossed, Names());

        Assert.False(imported.IsSuccess);
        Assert.Equal(MessageCodes.ValidationFailed, imported.Messages[0].Code);
        Assert.Equal("account", File.ReadAllText(Path.Combine(target, "core_user_7417348.dat")));
        Assert.Empty(backups.List());
    }

    [Fact]
    public void Import_RefusesTwoLinesWritingTheSameFile()
    {
        var source = _NewProfile("settings_Default");
        _WriteFile(source, "core_char_90250177.dat", "one");
        _WriteFile(source, "core_char_90382598.dat", "two");

        var target = Path.Combine(_root, "other-pc", "settings_Default");
        Directory.CreateDirectory(target);
        _WriteFile(target, "core_char_2123169375.dat", "here");

        var (presets, _) = _Services();
        var sourceProfile = EveSettingsLocator.LoadProfile(source);
        Assert.True(presets.Export(_PresetPath, "default", PresetScope.Selection, sourceProfile,
            sourceProfile.Characters, Names()).IsSuccess);

        var targetProfile = EveSettingsLocator.LoadProfile(target);
        var preset = presets.Read(_PresetPath).Value!;
        var only = targetProfile.Characters.Single();
        var both = preset.Manifest.Contents.Entries
            .Select(entry => new PresetImportItem(entry, PresetImportAction.Overwrite, only, only.FileName, "Lyra"))
            .ToList();

        var imported = presets.Import(preset, new PresetImportPlan(targetProfile, "install", both), Names());

        Assert.False(imported.IsSuccess);
        Assert.Contains("Two lines both write", imported.Messages[0].Text);
        Assert.Equal("here", File.ReadAllText(Path.Combine(target, "core_char_2123169375.dat")));
    }

    /// <summary>A client running blocks an import as surely as it blocks a copy, and for the same reason.</summary>
    [Fact]
    public void Import_DoesNothingWhileAnEveClientIsRunning()
    {
        var source = _NewProfile("settings_Default");
        _WriteFile(source, "core_char_90250177.dat", "layout");

        var target = Path.Combine(_root, "other-pc", "settings_Default");
        Directory.CreateDirectory(target);

        var (presets, backups) = _Services();
        var sourceProfile = EveSettingsLocator.LoadProfile(source);
        Assert.True(presets.Export(_PresetPath, "default", PresetScope.Selection, sourceProfile,
            sourceProfile.Characters, Names()).IsSuccess);

        var targetProfile = EveSettingsLocator.LoadProfile(target);
        var preset = presets.Read(_PresetPath).Value!;
        var plan = SettingsPresetService.BuildPlan(preset, targetProfile, "install", EveSettingsNames.Empty);

        var imported = presets.Import(preset, plan, Names(), abortWhen: () => true);

        Assert.False(imported.IsSuccess);
        Assert.Empty(Directory.GetFiles(target));
        Assert.Empty(backups.List());
    }

    // Writes a preset by hand, so a manifest this build would never produce (a future version, a file name EVE
    // would not have written) can be put in front of the reader.
    private static void _WriteRawPreset(
        string path,
        int formatVersion,
        (string FileName, SettingsFileKind Kind, long Id, string Name)[] entries,
        (string FileName, string Content)[] files)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var moment = "2026-08-29T20:00:00+00:00";
        var entryJson = string.Join(",", entries.Select(entry =>
            "{\"fileName\":\"" + entry.FileName.Replace("\\", "\\\\") + "\",\"kind\":\"" + entry.Kind +
            "\",\"id\":" + entry.Id + ",\"name\":\"" + entry.Name + "\",\"lastModifiedUtc\":\"" + moment +
            "\",\"sizeBytes\":10}"));
        var json =
            "{\"formatVersion\":" + formatVersion + ",\"name\":\"future\",\"createdAtUtc\":\"" + moment + "\"," +
            "\"appVersion\":\"9.9.9\",\"scope\":\"Selection\",\"contents\":{" +
            "\"formatVersion\":1,\"createdAtUtc\":\"" + moment + "\",\"reason\":\"Manual\",\"note\":\"\"," +
            "\"profileName\":\"settings_Default\",\"profileDirectory\":\"\",\"installRoot\":\"\"," +
            "\"appVersion\":\"9.9.9\",\"entries\":[" + entryJson + "]}}";

        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(archive.CreateEntry(SettingsPreset.ManifestFileName).Open()))
            writer.Write(json);
        foreach (var (fileName, content) in files)
        {
            using var writer = new StreamWriter(archive.CreateEntry($"files/{fileName}").Open());
            writer.Write(content);
        }
    }
}
