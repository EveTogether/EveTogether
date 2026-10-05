using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.LocalApi.Dtos;
using EveUtils.Client.LocalApi.Widgets;

namespace EveUtils.Client.ViewModels.Widgets;

/// <summary>
/// The editor of one widget. A built-in preset is edited as a draft and saved as a copy under My widgets; a saved
/// widget is saved in place, and an open OBS source picks the change up without a reload.
/// </summary>
public sealed partial class WidgetEditorViewModel : ObservableObject
{
    private static readonly (string Hex, string Label)[] AccentSwatches =
    [
        (WidgetPresets.DefaultAccent, "Blue"), ("#e3b341", "Amber"), ("#cb4d3e", "Red"),
        ("#4ec79e", "Green"), ("#b07cff", "Violet"), ("#c9d1d9", "Grey")
    ];

    private readonly WidgetManagerViewModel _manager;
    private readonly WidgetConfig _source;
    private bool _isBuilding = true;

    public WidgetEditorViewModel(WidgetManagerViewModel manager, WidgetConfig source,
        IReadOnlyList<(int Id, string Name)> characters, bool includeLocation)
    {
        _manager = manager;
        _source = source;
        Definition = WidgetPresets.Definition(source.Preset);
        IsBuiltIn = WidgetPresets.IsBuiltIn(source.Id);
        IncludeLocation = includeLocation;

        Name = source.Name;
        Characters = [.. characters.Select(character => new WidgetCharacterChipViewModel(
            character.Id, character.Name, source.CharacterIds.Contains(character.Id), _Changed))];
        Fields = [.. Definition.Fields.Select(field => new WidgetFieldViewModel(field,
            source.Fields.Contains(field.Key) && (!field.IsLocation || includeLocation),
            !field.IsLocation || includeLocation, _Changed))];
        Options = [.. Definition.Options.Select(option => new WidgetOptionViewModel(option,
            source.Options.TryGetValue(option.Key, out var value) ? value : option.DefaultValue, _Changed))];
        Themes = _Choices([("together", "together"), ("minimal", "minimal"), ("ticker", "ticker")],
            _ThemeKey(source.Theme), picked => _theme = picked);
        Accents = _Choices(AccentSwatches, source.Accent, picked => _accent = picked);
        Backgrounds = _Choices([("transparent", "transparent"), ("panel", "panel")],
            source.Background is WidgetBackground.Panel ? "panel" : "transparent", picked => _background = picked);
        _theme = _ThemeKey(source.Theme);
        _accent = source.Accent;
        _background = source.Background is WidgetBackground.Panel ? "panel" : "transparent";
        PanelOpacity = source.PanelOpacity;
        Scale = source.Scale;

        _isBuilding = false;
        _Refresh();
    }

    public WidgetPresetDefinition Definition { get; }
    public bool IsBuiltIn { get; }
    public bool IncludeLocation { get; }
    public string Id => _source.Id;
    public string Title => IsBuiltIn ? $"PRESET · {Definition.Defaults.Name.ToUpperInvariant()}" : "MY WIDGET";
    public string SaveLabel => IsBuiltIn ? "Save as my widget" : "Save";

    public IReadOnlyList<WidgetCharacterChipViewModel> Characters { get; }
    public IReadOnlyList<WidgetFieldViewModel> Fields { get; }
    public IReadOnlyList<WidgetOptionViewModel> Options { get; }
    public IReadOnlyList<WidgetChoiceViewModel> Themes { get; }
    public IReadOnlyList<WidgetChoiceViewModel> Accents { get; }
    public IReadOnlyList<WidgetChoiceViewModel> Backgrounds { get; }

    public bool HasCharacters => Characters.Count > 0;
    public bool HasOptions => Options.Count > 0;
    public bool HasLocationFields => Fields.Any(candidate => candidate.IsLocation);
    public bool IsAllCharacters => Characters.All(character => !character.IsOn);
    public bool IsPanel => _background == "panel";

    public string LocationNote => IncludeLocation
        ? "Location fields show only while \"Include my location\" is on and OPSEC mode is off."
        : "Location fields are off: turn on Settings › Integrations › \"Include my location\" to pick them.";

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private int _panelOpacity;
    [ObservableProperty] private int _scale;
    [ObservableProperty] private WidgetPreviewModel? _preview;
    [ObservableProperty] private string _url = "";
    [ObservableProperty] private bool _urlHasKey;
    [ObservableProperty] private int _width;
    [ObservableProperty] private int _height;
    [ObservableProperty] private bool _isObsGuideOpen;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _isError;
    [ObservableProperty] private bool _showsSessionReset;

    private string _theme;
    private string _accent;
    private string _background;

    public bool CanResetSession => _manager.CanResetSession;

    partial void OnNameChanged(string value) => _Changed();
    partial void OnPanelOpacityChanged(int value) => _Changed();
    partial void OnScaleChanged(int value) => _Changed();

    /// <summary>The config as edited right now.</summary>
    public WidgetConfig Build() => _source with
    {
        Name = Name.Trim(),
        CharacterIds = [.. Characters.Where(character => character.IsOn).Select(character => character.CharacterId)],
        Fields = [.. Fields.Where(field => field.IsOn).Select(field => field.Key)],
        Options = Options.ToDictionary(option => option.Key, option => option.Value),
        Theme = _theme switch { "minimal" => WidgetTheme.Minimal, "ticker" => WidgetTheme.Ticker, _ => WidgetTheme.Together },
        Accent = _accent,
        Background = IsPanel ? WidgetBackground.Panel : WidgetBackground.Transparent,
        PanelOpacity = PanelOpacity,
        Scale = Scale,
        ShowLocation = Fields.Any(field => field.IsOn && field.IsLocation)
    };

    public void ShowUrl(WidgetDto widget)
    {
        Url = widget.Url;
        UrlHasKey = widget.Url.Contains("?key=", StringComparison.Ordinal);
        Width = widget.Width;
        Height = widget.Height;
    }

    public void ShowStatus(string text, bool isError)
    {
        StatusText = text;
        IsError = isError;
    }

    [RelayCommand]
    private void AllCharacters()
    {
        foreach (var character in Characters)
            character.IsOn = false;
    }

    [RelayCommand]
    private void ToggleObsGuide() => IsObsGuideOpen = !IsObsGuideOpen;

    [RelayCommand]
    private void Back() => _manager.CloseEditor();

    [RelayCommand]
    private Task SaveAsync() => _manager.SaveAsync(this);

    [RelayCommand]
    private Task DeleteAsync() => _manager.DeleteAsync(this);

    [RelayCommand]
    private void ResetSession()
    {
        _manager.ResetSession();
        ShowStatus($"Session restarted at {DateTime.Now:HH:mm}.", isError: false);
    }

    private IReadOnlyList<WidgetChoiceViewModel> _Choices(IEnumerable<(string Value, string Label)> choices, string current,
        Action<string> apply)
    {
        List<WidgetChoiceViewModel> built = [];
        foreach (var (value, label) in choices)
        {
            built.Add(new WidgetChoiceViewModel(value, label, picked =>
            {
                foreach (var choice in built)
                    choice.IsOn = choice == picked;
                apply(picked.Value);
                OnPropertyChanged(nameof(IsPanel));
                _Changed();
            }) { IsOn = string.Equals(value, current, StringComparison.OrdinalIgnoreCase) });
        }
        return built;
    }

    private static string _ThemeKey(WidgetTheme theme) => theme.ToString().ToLowerInvariant();

    private void _Changed()
    {
        if (_isBuilding) return;
        _Refresh();
    }

    private void _Refresh()
    {
        var config = Build();
        Preview = WidgetPreviewModel.From(config);
        ShowUrl(_manager.WidgetFor(config));
        ShowsSessionReset = Options.Any(option => option.Key == "period" && option.Value == "session");
        OnPropertyChanged(nameof(IsAllCharacters));
    }
}
