using System.Collections.Generic;

namespace EveUtils.Client.LocalApi.Widgets;

/// <summary>One field a preset can show. <see cref="IsLocation"/> fields only show when the Local API exposes the location.</summary>
public sealed record WidgetFieldDefinition(string Key, string Label, WidgetFieldSource Source, bool IsLocation = false);

/// <summary>A choice for a preset option; the value is what the widget config stores.</summary>
public sealed record WidgetOptionChoice(string Value, string Label);

/// <summary>A preset option. No <see cref="Choices"/> = a whole number typed in (e.g. a minimum ISK).</summary>
public sealed record WidgetOptionDefinition(
    string Key, string Label, IReadOnlyList<WidgetOptionChoice> Choices, string? Default = null)
{
    public string DefaultValue => Default ?? (Choices.Count > 0 ? Choices[0].Value : "0");
}

/// <summary>Everything the manager shows about a built-in preset, beside its read-only <see cref="Defaults"/>.</summary>
public sealed record WidgetPresetDefinition(
    WidgetConfig Defaults,
    WidgetGroup Group,
    string Description,
    int BaseWidth,
    int BaseHeight,
    IReadOnlyList<WidgetFieldDefinition> Fields,
    IReadOnlyList<WidgetOptionDefinition> Options);
