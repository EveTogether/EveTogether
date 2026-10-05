using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.LocalApi.Widgets;

namespace EveUtils.Client.ViewModels.Widgets;

/// <summary>A library tile: a built-in preset (Customize saves a copy) or one of the pilot's own widgets (Edit).</summary>
public sealed partial class WidgetTileViewModel : ObservableObject
{
    private readonly Action<WidgetTileViewModel> _open;

    public WidgetTileViewModel(WidgetConfig config, string url, Action<WidgetTileViewModel> open)
    {
        _open = open;
        Config = config;
        var definition = WidgetPresets.Definition(config.Preset);
        IsBuiltIn = WidgetPresets.IsBuiltIn(config.Id);
        GroupLabel = definition.Group.ToString().ToUpperInvariant();
        Description = IsBuiltIn ? definition.Description : $"Your copy of {definition.Defaults.Name}.";
        Preview = WidgetPreviewModel.From(config);
        Url = url;
    }

    public WidgetConfig Config { get; }
    public bool IsBuiltIn { get; }
    public string Title => Config.Name;
    public string GroupLabel { get; }
    public string Description { get; }
    public string OpenLabel => IsBuiltIn ? "Customize" : "Edit";
    public WidgetPreviewModel Preview { get; }

    /// <summary>What OBS loads; carries the API key when one is set.</summary>
    [ObservableProperty] private string _url = "";

    [RelayCommand]
    private void Open() => _open(this);
}
