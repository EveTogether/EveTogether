using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.EveSettings;

namespace EveUtils.Client.ViewModels;

/// <summary>One character or account offered for a preset: ticked or not, with the write time that says how
/// recently it was the one you played on.</summary>
public partial class PresetEntryRowViewModel(EveSettingsFile file, string displayName) : ViewModelBase
{
    public EveSettingsFile File { get; } = file;

    public string DisplayName { get; } = displayName;

    public long Id => File.Id;

    public string IdDisplay => File.Id.ToString(CultureInfo.InvariantCulture);

    public bool ShowId => !DisplayName.EndsWith(IdDisplay, System.StringComparison.Ordinal);

    public string LastModifiedDisplay =>
        File.LastModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    [ObservableProperty] private bool _isIncluded;
}

/// <summary>
/// Saving a preset (ET-61): pick what goes in, give it a name, write it to one file you can carry to another
/// machine.
///
/// The pick is the point. "The whole account of Jithran and the character settings of Jithran, kept as my default"
/// is a deliberate subset, and that is what makes it useful — one good account and one good character are enough to
/// build a new machine from, with the ordinary sync spreading them from there. Taking everything at once is offered
/// too, as a switch rather than as the only option.
/// </summary>
public partial class PresetExportViewModel : ViewModelBase
{
    private readonly SettingsPresetService _presets;
    private readonly EveSettingsProfile _profile;
    private readonly string _installRoot;
    private readonly EveSettingsNames _names;

    public PresetExportViewModel(
        SettingsPresetService presets, EveSettingsProfile profile, string installRoot, EveSettingsNames names)
    {
        _presets = presets;
        _profile = profile;
        _installRoot = installRoot;
        _names = names;

        foreach (var file in profile.Characters.OrderBy(f => names.CharacterName(f.Id), System.StringComparer.OrdinalIgnoreCase))
            Characters.Add(_Row(file));
        foreach (var file in profile.Accounts.OrderBy(f => names.AccountName(f.Id), System.StringComparer.OrdinalIgnoreCase))
            Accounts.Add(_Row(file));
    }

    public ObservableCollection<PresetEntryRowViewModel> Characters { get; } = [];

    public ObservableCollection<PresetEntryRowViewModel> Accounts { get; } = [];

    public string ProfileName => _profile.Name;

    /// <summary>The name the user gives it, so several presets can be kept side by side.</summary>
    [ObservableProperty] private string _presetName = "default";

    /// <summary>Everything in the profile instead of the ticks — the "of course I also want all of it" case.</summary>
    [ObservableProperty] private bool _wholeProfile;

    [ObservableProperty] private string _status =
        "Tick what goes in, name it, then save it somewhere you can find it again.";

    [ObservableProperty] private bool _statusIsError;

    /// <summary>True once a file has been written — the dialog then reports where it went instead of offering to
    /// write it again over the top.</summary>
    [ObservableProperty] private bool _saved;

    public string SuggestedFileName => SettingsPresetService.SuggestedFileName(PresetName);

    public IReadOnlyList<EveSettingsFile> Selection() => WholeProfile
        ? _profile.Characters.Concat(_profile.Accounts).ToList()
        : Characters.Concat(Accounts).Where(row => row.IsIncluded).Select(row => row.File).ToList();

    public string Summary
    {
        get
        {
            var selected = Selection();
            if (selected.Count == 0)
                return "Nothing ticked yet — a preset needs at least one character or account.";

            var characters = selected.Count(file => file.Kind == SettingsFileKind.Character);
            var accounts = selected.Count - characters;
            return $"{characters} {(characters == 1 ? "character" : "characters")} and " +
                   $"{accounts} {(accounts == 1 ? "account" : "accounts")} from {_profile.Name}, in one file.";
        }
    }

    /// <summary>Said in the dialog rather than only in the code: this file gets passed around, so what is in it is
    /// the user's business.</summary>
    public const string ContentsNote =
        "A preset holds only the EVE settings files you tick and a description of them — kind, id, name and write " +
        "date, plus the date and EVE Together version it was made with. No login tokens, no session data, and no " +
        "folder paths (those would spell out your Windows account name).";

    public bool CanExport => !Saved && Selection().Count > 0 && !string.IsNullOrWhiteSpace(PresetName);

    [RelayCommand]
    private void SelectAll() => _SetAll(true);

    [RelayCommand]
    private void SelectNone() => _SetAll(false);

    /// <summary>Writes the preset. The caller supplies the path — the file picker belongs to the window.</summary>
    public async Task<bool> ExportToAsync(string filePath)
    {
        var selection = Selection();
        var result = await Task.Run(() => _presets.Export(
            filePath, PresetName, WholeProfile ? PresetScope.WholeProfile : PresetScope.Selection,
            _profile, selection, _names.AsLookup()));

        if (!result.IsSuccess || result.Value is null)
        {
            Status = string.Join(" ", result.Messages.Select(message => message.Text));
            StatusIsError = true;
            return false;
        }

        Saved = true;
        StatusIsError = false;
        Status = $"Saved \"{result.Value.Manifest.Name}\" — {Summary} Written to {filePath}. " +
                 "Copy it to the other machine and open it there with IMPORT PRESET.";
        OnPropertyChanged(nameof(CanExport));
        return true;
    }

    private PresetEntryRowViewModel _Row(EveSettingsFile file)
    {
        var row = new PresetEntryRowViewModel(file, _names.DisplayName(file));
        row.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(PresetEntryRowViewModel.IsIncluded))
                _Notify();
        };
        return row;
    }

    private void _SetAll(bool included)
    {
        foreach (var row in Characters.Concat(Accounts))
            row.IsIncluded = included;
        _Notify();
    }

    partial void OnPresetNameChanged(string value)
    {
        OnPropertyChanged(nameof(SuggestedFileName));
        OnPropertyChanged(nameof(CanExport));
    }

    partial void OnWholeProfileChanged(bool value) => _Notify();

    private void _Notify()
    {
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(CanExport));
    }
}
