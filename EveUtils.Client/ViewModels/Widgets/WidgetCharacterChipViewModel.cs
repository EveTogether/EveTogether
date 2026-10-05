using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace EveUtils.Client.ViewModels.Widgets;

/// <summary>A character the widget is saved with. None picked = every own character.</summary>
public sealed partial class WidgetCharacterChipViewModel(int characterId, string name, bool isOn, Action changed)
    : ObservableObject
{
    public int CharacterId { get; } = characterId;
    public string Name { get; } = name;

    [ObservableProperty] private bool _isOn = isOn;

    partial void OnIsOnChanged(bool value) => changed();

    [RelayCommand]
    private void Toggle() => IsOn = !IsOn;
}
