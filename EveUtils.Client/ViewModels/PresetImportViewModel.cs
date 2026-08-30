using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.EveSettings;
using EveUtils.Client.Platform;

namespace EveUtils.Client.ViewModels;

/// <summary>Where one entry of a preset can land: skipped, written over a file already here, or written as a file
/// this machine does not have yet.</summary>
public sealed record PresetTargetOption(
    string Label, PresetImportAction Action, EveSettingsFile? Target, string TargetFileName);

/// <summary>
/// One line of the import preview: what the preset holds and what will happen to it here. The choice is the user's —
/// character ids are the same everywhere in EVE so the match is usually obvious, but "usually" is not a thing to
/// overwrite settings on, so every line says where it is going and can be pointed somewhere else.
/// </summary>
public partial class PresetImportRowViewModel : ViewModelBase
{
    public PresetImportRowViewModel(SettingsBackupEntry entry, IReadOnlyList<PresetTargetOption> options, PresetTargetOption chosen)
    {
        Entry = entry;
        Options = new ObservableCollection<PresetTargetOption>(options);
        _selectedOption = chosen;
    }

    public SettingsBackupEntry Entry { get; }

    public ObservableCollection<PresetTargetOption> Options { get; }

    [ObservableProperty] private PresetTargetOption _selectedOption;

    public string SourceLabel => string.IsNullOrWhiteSpace(Entry.Name)
        ? $"{(Entry.Kind == SettingsFileKind.Character ? "Character" : "Account")} {IdDisplay}"
        : Entry.Name;

    public string IdDisplay => Entry.Id.ToString(CultureInfo.InvariantCulture);

    /// <summary>False when the fallback name already ends in the id — printing it twice on one row is noise.</summary>
    public bool ShowId => !SourceLabel.EndsWith(IdDisplay, StringComparison.Ordinal);

    public string KindLabel => Entry.Kind == SettingsFileKind.Character ? "CHARACTER" : "ACCOUNT";

    /// <summary>When the file in the preset was last written — an old one may hold settings from an older EVE.</summary>
    public string LastModifiedDisplay =>
        Entry.LastModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    public PresetImportAction Action => SelectedOption.Action;

    public string ActionDisplay => SelectedOption.Action switch
    {
        PresetImportAction.Overwrite => "OVERWRITES",
        PresetImportAction.New => "NEW FILE",
        _ => "SKIPPED"
    };

    public bool IsOverwrite => Action == PresetImportAction.Overwrite;

    public bool IsNew => Action == PresetImportAction.New;

    public bool IsSkip => Action == PresetImportAction.Skip;

    public PresetImportItem ToItem() => new(
        Entry, SelectedOption.Action, SelectedOption.Target, SelectedOption.TargetFileName, SelectedOption.Label);

    partial void OnSelectedOptionChanged(PresetTargetOption value)
    {
        OnPropertyChanged(nameof(Action));
        OnPropertyChanged(nameof(ActionDisplay));
        OnPropertyChanged(nameof(IsOverwrite));
        OnPropertyChanged(nameof(IsNew));
        OnPropertyChanged(nameof(IsSkip));
    }
}

/// <summary>
/// Reading a preset back in (ET-61) — the most far-reaching thing this tool does, so it is also the slowest to act.
/// Open the file, see exactly what is in it and where every line would land, change any line, and only then apply,
/// with the whole profile backed up first.
///
/// On a fresh machine most lines will say NEW FILE: EVE writes no <c>core_char_&lt;id&gt;.dat</c> until that
/// character has logged in once, and putting the file there first is the whole point of carrying a preset over.
/// </summary>
public partial class PresetImportViewModel : ViewModelBase
{
    private readonly SettingsPresetService _presets;
    private readonly EveSettingsProfile _profile;
    private readonly string _installRoot;
    private readonly EveSettingsNames _names;
    private readonly EveClientPresenceService? _presence;

    public PresetImportViewModel(
        SettingsPresetService presets,
        EveSettingsProfile profile,
        string installRoot,
        EveSettingsNames names,
        EveClientPresenceService? presence = null)
    {
        _presets = presets;
        _profile = profile;
        _installRoot = installRoot;
        _names = names;
        _presence = presence;
        CheckClients();
    }

    public ObservableCollection<PresetImportRowViewModel> Rows { get; } = [];

    public SettingsPreset? Preset { get; private set; }

    public string TargetProfile => $"{_profile.Name} — {_profile.Characters.Count} characters, " +
                                   $"{_profile.Accounts.Count} accounts on this machine";

    [ObservableProperty] private string _status =
        "Choose a preset file. Nothing is written until you have seen what it would do and pressed apply.";

    [ObservableProperty] private bool _statusIsError;

    [ObservableProperty] private bool _hasPreset;

    /// <summary>True once files have been written — the dialog then reports what happened rather than offering to
    /// do it again.</summary>
    [ObservableProperty] private bool _applied;

    [ObservableProperty] private bool _clientsRunning;

    [ObservableProperty] private string _clientWarning = string.Empty;

    /// <summary>Name, when it was made and with which build — an old preset can hold settings from an older EVE, and
    /// that is only visible if it is said.</summary>
    [ObservableProperty] private string _presetHeader = string.Empty;

    [ObservableProperty] private string _presetOrigin = string.Empty;

    /// <summary>Set when the preset comes from a newer EVE Together: it is described in full and applied to nothing.</summary>
    [ObservableProperty] private string _versionWarning = string.Empty;

    public bool HasVersionWarning => !string.IsNullOrEmpty(VersionWarning);

    public string PlanSummary
    {
        get
        {
            if (!HasPreset)
                return string.Empty;

            var overwrite = Rows.Count(row => row.IsOverwrite);
            var created = Rows.Count(row => row.IsNew);
            var skipped = Rows.Count(row => row.IsSkip);
            return $"{overwrite} overwritten, {created} new, {skipped} skipped. " +
                   $"The whole of {_profile.Name} is backed up first.";
        }
    }

    public bool CanApply => HasPreset && !Applied && !ClientsRunning &&
                            Preset is { CanApply: true } && Rows.Any(row => !row.IsSkip);

    /// <summary>Reads a preset file and lays out what it would do. Writes nothing.</summary>
    public async Task LoadAsync(string filePath)
    {
        var result = await Task.Run(() => _presets.Read(filePath));
        Rows.Clear();
        Preset = null;
        HasPreset = false;
        VersionWarning = string.Empty;

        if (!result.IsSuccess || result.Value is null)
        {
            Status = string.Join(" ", result.Messages.Select(message => message.Text));
            StatusIsError = true;
            _Notify();
            return;
        }

        var preset = result.Value;
        Preset = preset;
        HasPreset = true;
        StatusIsError = false;

        var manifest = preset.Manifest;
        PresetHeader = $"\"{manifest.Name}\" — {manifest.ScopeSummary}, {manifest.Contents.ContentsSummary}";
        PresetOrigin = $"Made {manifest.CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} " +
                       $"with EVE Together {manifest.AppVersion}";
        if (!preset.CanApply)
            VersionWarning = $"This preset was written by a newer version of EVE Together (format {manifest.FormatVersion}). " +
                             "It is shown in full, but nothing from it can be written here.";

        foreach (var item in SettingsPresetService.BuildPlan(preset, _profile, _installRoot, _names).Items)
            Rows.Add(_Row(item));

        Status = preset.CanApply
            ? "Check every line below — that is exactly what will happen — then apply."
            : VersionWarning;
        _Notify();
    }

    /// <summary>
    /// Re-checks for running EVE clients. Same rule as the rest of the tool, for the same reason: EVE writes its
    /// settings back out when it closes, so anything imported into a running client is undone at logout.
    /// </summary>
    [RelayCommand]
    public void CheckClients()
    {
        if (_presence is null)
        {
            ClientsRunning = false;
            ClientWarning = string.Empty;
            _Notify();
            return;
        }

        var processes = _presence.RunningClientCount();
        var inGame = _presence.Current.CharacterNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
        ClientsRunning = processes > 0 || inGame.Count > 0;
        ClientWarning = ClientsRunning
            ? $"{Math.Max(processes, inGame.Count)} EVE client(s) running" +
              (inGame.Count > 0 ? $" ({string.Join(", ", inGame)})" : string.Empty) +
              ". EVE rewrites its settings when it closes, so an import now is undone at logout. Close every client, then check again."
            : string.Empty;
        _Notify();
    }

    [RelayCommand]
    private async Task ApplyAsync()
    {
        if (Preset is null || !CanApply)
            return;

        CheckClients();
        if (ClientsRunning)
        {
            Status = ClientWarning;
            StatusIsError = true;
            return;
        }

        var preset = Preset;
        var plan = new PresetImportPlan(_profile, _installRoot, Rows.Select(row => row.ToItem()).ToList());
        var names = _names.AsLookup();
        var result = await Task.Run(() => _presets.Import(preset, plan, names, abortWhen: _ClientsRunningNow));

        if (!result.IsSuccess || result.Value is null)
        {
            Status = string.Join(" ", result.Messages.Select(message => message.Text));
            StatusIsError = true;
            return;
        }

        var outcome = result.Value;
        Applied = true;
        StatusIsError = outcome.Failed.Count > 0;
        Status = $"Imported \"{preset.Manifest.Name}\" into {_profile.Name}: " +
                 $"{outcome.Overwritten.Count} overwritten, {outcome.Created.Count} written new, " +
                 $"{outcome.Skipped.Count} skipped." +
                 (outcome.Failed.Count > 0 ? $" Not written: {string.Join("; ", outcome.Failed)}." : string.Empty) +
                 $" The profile as it was is in {outcome.Backup.DirectoryPath}. " +
                 "Use the two copy blocks in the tool to spread these onto your other characters and accounts.";
        _Notify();
    }

    private bool _ClientsRunningNow() =>
        _presence is not null && (_presence.RunningClientCount() > 0 || _presence.Current.CharacterNames.Count > 0);

    private PresetImportRowViewModel _Row(PresetImportItem item)
    {
        var options = new List<PresetTargetOption> { new("Skip — leave this machine alone", PresetImportAction.Skip, null, item.Entry.FileName) };

        // Only offered when this machine has no file with that id: writing it under its own name is what a fresh
        // install needs, and offering it beside an existing file of the same id would mean two lines writing one file.
        var here = (item.Entry.Kind == SettingsFileKind.Character ? _profile.Characters : _profile.Accounts).ToList();
        if (here.All(file => file.Id != item.Entry.Id))
            options.Add(new PresetTargetOption($"New file — {item.Entry.FileName}", PresetImportAction.New, null, item.Entry.FileName));

        foreach (var file in here.OrderBy(file => _names.DisplayName(file), StringComparer.OrdinalIgnoreCase))
            options.Add(new PresetTargetOption($"Overwrite {_names.DisplayName(file)} · {file.Id}",
                PresetImportAction.Overwrite, file, file.FileName));

        var chosen = options.FirstOrDefault(option =>
            option.Action == item.Action && option.TargetFileName == item.TargetFileName) ?? options[0];

        var row = new PresetImportRowViewModel(item.Entry, options, chosen);
        row.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(PresetImportRowViewModel.SelectedOption))
                _Notify();
        };
        return row;
    }

    private void _Notify()
    {
        OnPropertyChanged(nameof(PlanSummary));
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(HasVersionWarning));
    }

    partial void OnHasPresetChanged(bool value) => _Notify();

    partial void OnAppliedChanged(bool value) => _Notify();

    partial void OnClientsRunningChanged(bool value) => _Notify();

    partial void OnVersionWarningChanged(string value) => OnPropertyChanged(nameof(HasVersionWarning));
}
