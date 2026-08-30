using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using EveUtils.Shared.App;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;

namespace EveUtils.Client.EveSettings;

/// <summary>
/// Writes a preset out to one file and reads one back in (ET-61) — the way settings travel to another machine.
///
/// A preset is a zip holding <c>preset.json</c> and a <c>files/</c> folder: the backup format from ET-59 with a
/// jacket on, not a second format. Two ways to use it, and the first is the point: pick one account and one
/// character, save that as your starting point, and on the new machine put it down and spread it from there with the
/// ordinary sync. The second is simply everything at once.
///
/// <para><b>What goes in, exactly.</b> The chosen <c>core_char_&lt;id&gt;.dat</c> / <c>core_user_&lt;id&gt;.dat</c>
/// files, and a manifest describing them: format version, the preset's name, when it was made, which EVE Together
/// built it, whether it is a selection or a whole profile, the profile's <em>name</em>, and per file the kind, the
/// id, the display name and the write time. Nothing else. In particular the manifest's directory fields are blanked
/// on the way out (<see cref="_Redact"/>): they hold <c>C:\Users\&lt;someone&gt;\...</c>, and a preset is a file the
/// user passes to another person. No tokens, no session state, no machine identity — none of that is in the settings
/// files or in the manifest to begin with, and the export copies nothing else.</para>
///
/// <para>Reading one back is treated as untrusted input: only entries whose names EVE itself would have written are
/// unpacked, and each one goes to a file name this machine decides on, never one the archive supplies.</para>
/// </summary>
public sealed class SettingsPresetService(SettingsBackupService backups) : ISingletonService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// Writes <paramref name="selection"/> to <paramref name="filePath"/> as one portable file.
    /// </summary>
    public Result<SettingsPreset> Export(
        string filePath,
        string presetName,
        PresetScope scope,
        EveSettingsProfile profile,
        IReadOnlyList<EveSettingsFile> selection,
        IReadOnlyDictionary<long, string> names)
    {
        if (string.IsNullOrWhiteSpace(presetName))
            return _Refuse<SettingsPreset>("Give the preset a name, so you can tell it apart from the next one.");

        if (selection.Count == 0)
            return _Refuse<SettingsPreset>("Pick at least one character or account to put in the preset.");

        var manifest = new PresetManifest
        {
            Name = presetName.Trim(),
            CreatedAtUtc = DateTimeOffset.UtcNow,
            AppVersion = AppInfo.Version,
            Scope = scope,
            Contents = new SettingsBackupManifest
            {
                CreatedAtUtc = DateTimeOffset.UtcNow,
                Reason = BackupReason.Manual,
                Note = $"preset \"{presetName.Trim()}\"",
                ProfileName = profile.Name,
                ProfileDirectory = _Redact,   // a path names the user's Windows account; a preset is passed around
                InstallRoot = _Redact,
                AppVersion = AppInfo.Version,
                Entries = selection.Select(file => new SettingsBackupEntry(
                    file.FileName, file.Kind, file.Id,
                    names.TryGetValue(file.Id, out var name) ? name : string.Empty,
                    file.LastModifiedUtc, file.SizeBytes)).ToList()
            }
        };

        try
        {
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            using (var archive = ZipFile.Open(filePath, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry(SettingsPreset.ManifestFileName);
                using (var writer = new StreamWriter(entry.Open()))
                    writer.Write(JsonSerializer.Serialize(manifest, JsonOptions));

                foreach (var file in selection)
                    archive.CreateEntryFromFile(file.FullPath, $"{SettingsPreset.FilesFolderName}/{file.FileName}");
            }

            return Result<SettingsPreset>.Success(new SettingsPreset(filePath, manifest));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _TryDelete(filePath);   // a half-written preset would read as a truncated one somewhere else
            return Result<SettingsPreset>.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.FileIoFailed,
                $"Could not write the preset: {ex.Message}"));
        }
    }

    /// <summary>
    /// Reads a preset's manifest without writing anything. A file that is not one, or whose manifest lists a name EVE
    /// would never have written, is refused here rather than part-way through an import.
    /// </summary>
    public Result<SettingsPreset> Read(string filePath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(filePath);
            var manifestEntry = archive.GetEntry(SettingsPreset.ManifestFileName);
            if (manifestEntry is null)
                return _Refuse<SettingsPreset>("That file is not an EVE Together preset: it has no preset.json inside.");

            using var reader = new StreamReader(manifestEntry.Open());
            var manifest = JsonSerializer.Deserialize<PresetManifest>(reader.ReadToEnd(), JsonOptions);
            if (manifest is null)
                return _Refuse<SettingsPreset>("That preset could not be read.");

            foreach (var entry in manifest.Contents.Entries)
            {
                if (!EveSettingsLocator.TryReadSettingsFileName(entry.FileName, out var kind, out var id))
                    return _Refuse<SettingsPreset>(
                        $"That preset lists a file EVE would not have written ({entry.FileName}); it was not opened.");
                if (kind != entry.Kind || id != entry.Id)
                    return _Refuse<SettingsPreset>(
                        $"That preset describes {entry.FileName} as something it is not; it was not opened.");
            }

            return Result<SettingsPreset>.Success(new SettingsPreset(filePath, manifest));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            return Result<SettingsPreset>.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.ParseError,
                $"Could not read that preset: {ex.Message}"));
        }
    }

    /// <summary>
    /// What importing this preset into this profile would do, line by line, before anything is written. Character ids
    /// are the same everywhere in EVE, so an entry whose id is already here defaults to overwriting that file; one
    /// that is not lands as a new file, which on a fresh install is the normal case rather than the edge one. The
    /// caller may change any line — including onto a different character or account of the same kind, for the machine
    /// where the ids did not line up after all.
    /// </summary>
    public static PresetImportPlan BuildPlan(
        SettingsPreset preset, EveSettingsProfile profile, string installRoot, EveSettingsNames names)
    {
        var items = new List<PresetImportItem>();
        foreach (var entry in preset.Manifest.Contents.Entries.OrderBy(entry => entry.Kind).ThenBy(entry => entry.Name))
        {
            var here = (entry.Kind == SettingsFileKind.Character ? profile.Characters : profile.Accounts)
                .FirstOrDefault(file => file.Id == entry.Id);

            items.Add(here is null
                ? new PresetImportItem(entry, PresetImportAction.New, null, entry.FileName,
                    _EntryLabel(entry) + " (not on this machine yet)")
                : new PresetImportItem(entry, PresetImportAction.Overwrite, here, here.FileName,
                    names.DisplayName(here)));
        }

        return new PresetImportPlan(profile, installRoot, items);
    }

    /// <summary>
    /// Writes the preset over the profile, after backing the whole profile up — the same rule as every other write in
    /// this tool. A fresh profile with nothing in it is the one case where the snapshot is empty rather than refused:
    /// the most far-reaching action in the tool must not be the only one that runs without a record.
    /// </summary>
    public Result<PresetImportOutcome> Import(
        SettingsPreset preset,
        PresetImportPlan plan,
        IReadOnlyDictionary<long, string> names,
        Func<bool>? abortWhen = null)
    {
        if (!preset.CanApply)
            return _Refuse<PresetImportOutcome>(
                $"This preset was written by a newer version of EVE Together (format {preset.Manifest.FormatVersion}). " +
                "It is shown in full, but nothing from it is written here.");

        var writing = plan.Items.Where(item => item.Action != PresetImportAction.Skip).ToList();
        if (writing.Count == 0)
            return _Refuse<PresetImportOutcome>("Every line is set to skip, so there is nothing to import.");

        foreach (var item in writing)
        {
            if (item.Target is not null && item.Target.Kind != item.Entry.Kind)
                return _Refuse<PresetImportOutcome>(
                    "Character settings and account settings cannot be written onto each other.");

            if (!EveSettingsLocator.TryReadSettingsFileName(item.TargetFileName, out var kind, out _) ||
                kind != item.Entry.Kind)
                return _Refuse<PresetImportOutcome>($"{item.TargetFileName} is not a settings file this can write.");
        }

        var collision = writing.GroupBy(item => item.TargetFileName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (collision is not null)
            return _Refuse<PresetImportOutcome>(
                $"Two lines both write to {collision.Key}. Point one of them somewhere else, or skip it.");

        if (abortWhen?.Invoke() == true)
            return _Refuse<PresetImportOutcome>("An EVE client is running, so nothing was imported.");

        using var gate = EveSettingsWriteGate.Acquire();   // never interleaved with a sync writing the same profile

        var backup = backups.Create(plan.Profile, plan.InstallRoot, names, BackupReason.BeforeImport,
            $"before importing the preset \"{preset.Manifest.Name}\"", allowEmpty: true);
        if (!backup.IsSuccess || backup.Value is null)
            return Result<PresetImportOutcome>.Failure(backup.Messages.ToArray());

        var overwritten = new List<string>();
        var created = new List<string>();
        var failed = new List<string>();
        var skipped = plan.Items.Where(item => item.Action == PresetImportAction.Skip)
            .Select(item => _EntryLabel(item.Entry)).ToList();

        try
        {
            using var archive = ZipFile.OpenRead(preset.FilePath);
            foreach (var item in writing)
            {
                var source = archive.GetEntry($"{SettingsPreset.FilesFolderName}/{item.Entry.FileName}");
                if (source is null)
                {
                    failed.Add($"{_EntryLabel(item.Entry)}: missing from the preset");
                    continue;
                }

                try
                {
                    // The destination is composed here from the profile directory and a file name this machine
                    // validated — never from a path inside the archive.
                    source.ExtractToFile(Path.Combine(plan.Profile.DirectoryPath, item.TargetFileName), overwrite: true);
                    (item.Action == PresetImportAction.New ? created : overwritten)
                        .Add($"{_EntryLabel(item.Entry)} → {item.TargetFileName}");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failed.Add($"{_EntryLabel(item.Entry)}: {ex.Message}");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return Result<PresetImportOutcome>.Failure(new ResultMessage(MessageSeverity.Error,
                MessageCodes.FileIoFailed, $"Could not read the preset: {ex.Message}"));
        }

        return Result<PresetImportOutcome>.Success(
            new PresetImportOutcome(overwritten, created, skipped, failed, backup.Value));
    }

    /// <summary>A file name for a preset that is safe on any OS and says what it is.</summary>
    public static string SuggestedFileName(string presetName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var stem = string.Concat((string.IsNullOrWhiteSpace(presetName) ? "preset" : presetName.Trim())
            .Select(c => invalid.Contains(c) ? '_' : c));
        return stem + SettingsPreset.FileExtension;
    }

    /// <summary>What a path field is replaced with on export. Not an empty string: reading one back should say why
    /// it is blank rather than look like something went missing.</summary>
    private const string _Redact = "(not stored in a preset)";

    private static string _EntryLabel(SettingsBackupEntry entry) => string.IsNullOrWhiteSpace(entry.Name)
        ? $"{(entry.Kind == SettingsFileKind.Character ? "Character" : "Account")} {entry.Id}"
        : $"{entry.Name} · {entry.Id}";

    private static Result<T> _Refuse<T>(string message) =>
        Result<T>.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.ValidationFailed, message));

    private static void _TryDelete(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
                File.Delete(filePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The caller already reports the write failure; a stub left behind is the lesser problem.
        }
    }
}
