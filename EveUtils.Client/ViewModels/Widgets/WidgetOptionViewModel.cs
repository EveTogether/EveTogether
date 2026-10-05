using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Client.LocalApi.Widgets;

namespace EveUtils.Client.ViewModels.Widgets;

/// <summary>A preset option: a segmented choice, or a whole number typed in when the option has no choices.</summary>
public sealed partial class WidgetOptionViewModel : ObservableObject
{
    private readonly Action _changed;

    public WidgetOptionViewModel(WidgetOptionDefinition definition, string value, Action changed)
    {
        _changed = changed;
        Key = definition.Key;
        Label = definition.Label;
        Choices = [.. definition.Choices.Select(choice => new WidgetChoiceViewModel(choice.Value, choice.Label, _Pick))];
        Value = value;
        Text = value;
        _ShowPicked();
    }

    public string Key { get; }
    public string Label { get; }
    public IReadOnlyList<WidgetChoiceViewModel> Choices { get; }
    public bool IsNumber => Choices.Count == 0;

    [ObservableProperty] private string _value = "";

    /// <summary>The typed number; only digits reach <see cref="Value"/>, so the saved config stays a whole number.</summary>
    [ObservableProperty] private string _text = "";

    partial void OnTextChanged(string value)
    {
        if (value.Length > 0 && value.All(char.IsAsciiDigit))
            Value = value;
    }

    partial void OnValueChanged(string value) => _changed();

    private void _Pick(WidgetChoiceViewModel choice)
    {
        Value = choice.Value;
        _ShowPicked();
    }

    private void _ShowPicked()
    {
        foreach (var choice in Choices)
            choice.IsOn = choice.Value == Value;
    }
}
