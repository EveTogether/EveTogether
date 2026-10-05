using System.Collections.Generic;

namespace EveUtils.Client.LocalApi.Widgets;

/// <summary>
/// One widget's configuration: a built-in preset (id = the preset key) or a saved copy (id = 32 hex chars). Field and
/// option keys are owned by the preset that reads them; a field missing from <see cref="Fields"/> is off.
/// </summary>
public sealed record WidgetConfig
{
    public string Id { get; init; } = "";
    public WidgetPreset Preset { get; init; }
    public string Name { get; init; } = "";

    /// <summary>The characters this widget shows, saved with it (no active character). Empty = every own character.</summary>
    public IReadOnlyList<int> CharacterIds { get; init; } = [];

    public IReadOnlyList<string> Fields { get; init; } = [];
    public IReadOnlyDictionary<string, string> Options { get; init; } = new Dictionary<string, string>();
    public WidgetTheme Theme { get; init; } = WidgetTheme.Together;
    public string Accent { get; init; } = WidgetPresets.DefaultAccent;
    public WidgetBackground Background { get; init; } = WidgetBackground.Transparent;

    /// <summary>Panel opacity in percent; only used with <see cref="WidgetBackground.Panel"/>.</summary>
    public int PanelOpacity { get; init; } = 60;

    /// <summary>Size in percent of the preset's base size; sets the width × height for OBS.</summary>
    public int Scale { get; init; } = 100;

    /// <summary>Location fields on this widget; they only ever show when the Local API itself exposes the location.</summary>
    public bool ShowLocation { get; init; }
}
