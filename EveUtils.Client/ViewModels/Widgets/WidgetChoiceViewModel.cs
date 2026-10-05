using System;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace EveUtils.Client.ViewModels.Widgets;

/// <summary>One button of a segmented choice (an option value, a theme, an accent swatch).</summary>
public sealed partial class WidgetChoiceViewModel(string value, string label, Action<WidgetChoiceViewModel> picked)
    : ObservableObject
{
    public string Value { get; } = value;
    public string Label { get; } = label;

    /// <summary>The colour of an accent swatch; transparent for a choice whose value is not a colour.</summary>
    public IBrush Swatch { get; } = Color.TryParse(value, out var colour) ? new SolidColorBrush(colour) : Brushes.Transparent;

    [ObservableProperty] private bool _isOn;

    [RelayCommand]
    private void Pick() => picked(this);
}
