using System;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Client.LocalApi.Widgets;

namespace EveUtils.Client.ViewModels.Widgets;

/// <summary>A field the widget can show, with where its data comes from. A location field can only be switched on
/// while the Local API is allowed to share the location.</summary>
public sealed partial class WidgetFieldViewModel(WidgetFieldDefinition definition, bool isOn, bool canChoose, Action changed)
    : ObservableObject
{
    public string Key { get; } = definition.Key;
    public string Label { get; } = definition.Label;
    public bool IsLocation { get; } = definition.IsLocation;
    public bool CanChoose { get; } = canChoose;
    public string SourceLabel { get; } = definition.Source.ToString().ToUpperInvariant();
    public bool IsServed { get; } = definition.Source is WidgetFieldSource.Api;

    public string SourceTip { get; } = definition.Source switch
    {
        WidgetFieldSource.Api => "Served by the Local API today.",
        WidgetFieldSource.App => "Known to the app but not served to widgets yet, so the widget leaves it empty.",
        _ => "Not collected yet, so the widget leaves it empty."
    };

    [ObservableProperty] private bool _isOn = isOn;

    partial void OnIsOnChanged(bool value) => changed();
}
