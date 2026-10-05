using System;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Shared.Modules.Gamelog.Models;

namespace EveUtils.Client.ViewModels.GameLogs;

/// <summary>A type chip of the GAME LOGS filter row: on = lines of that kind are shown. All start on.</summary>
public sealed partial class GameLogKindChipViewModel(GameLogLineKind kind, Action changed) : ObservableObject
{
    public GameLogLineKind Kind { get; } = kind;

    public string Label { get; } = GameLogKindPalette.LabelOf(kind);

    public IBrush Brush { get; } = GameLogKindPalette.BrushOf(kind);

    [ObservableProperty] private bool _isOn = true;

    /// <summary>How many lines of this kind the character filter and the search leave, whatever the chips say.</summary>
    [ObservableProperty] private int _count;

    partial void OnIsOnChanged(bool value) => changed();

    [RelayCommand]
    private void Toggle() => IsOn = !IsOn;
}
