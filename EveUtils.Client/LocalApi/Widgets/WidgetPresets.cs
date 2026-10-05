using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace EveUtils.Client.LocalApi.Widgets;

/// <summary>
/// The built-in presets with their defaults. They live in code only and are never stored, so they cannot be changed:
/// customizing one saves a copy under a new id. Location fields are off in every preset.
/// </summary>
public static class WidgetPresets
{
    public const string DefaultAccent = "#4fc3f7";

    public static IReadOnlyList<WidgetConfig> All { get; } =
    [
        _Preset(WidgetPreset.LiveDps, "Live DPS", ["name", "dpsOut", "dpsIn", "peak"]),
        _Preset(WidgetPreset.DpsGraph, "DPS graph", ["name", "dpsOut", "dpsIn", "reps", "numbers"],
            new() { ["span"] = "60" }),
        _Preset(WidgetPreset.CurrentRun, "Current run", ["type", "site", "tier", "clock", "loot", "bounty", "total", "kills"]),
        _Preset(WidgetPreset.RunTotals, "Run totals", ["runs", "isk", "iskPerHour", "flownTime", "bestDrop"],
            new() { ["period"] = "session", ["kind"] = "all" }),
        _Preset(WidgetPreset.AbyssalTotals, "Abyssal totals", ["runs", "isk", "iskPerHour", "averageClear", "byTier"],
            new() { ["period"] = "session" }),
        _Preset(WidgetPreset.LastKillmail, "Last killmail", ["badge", "ship", "isk", "attackers", "ago", "run", "victim"],
            new() { ["show"] = "both" }),
        _Preset(WidgetPreset.KillAlert, "Kill alert", ["ship", "victim", "isk"],
            new() { ["holdSeconds"] = "8", ["minIsk"] = "0" }),
        _Preset(WidgetPreset.FleetDps, "Fleet DPS", ["names", "dpsOut", "dpsIn", "total"])
    ];

    /// <summary>The preset's fixed id and URL segment, e.g. <c>live-dps</c>.</summary>
    public static string Key(WidgetPreset preset) => JsonSerializer.Serialize(preset).Trim('"');

    public static WidgetConfig? Find(string id) => All.FirstOrDefault(preset => preset.Id == id);

    public static bool IsBuiltIn(string id) => Find(id) is not null;

    /// <summary>Width × height in pixels at 100 % scale — what OBS's browser source needs.</summary>
    public static (int Width, int Height) BaseSize(WidgetPreset preset) => preset switch
    {
        WidgetPreset.LiveDps => (320, 120),
        WidgetPreset.DpsGraph => (480, 200),
        WidgetPreset.CurrentRun => (360, 180),
        WidgetPreset.RunTotals => (360, 160),
        WidgetPreset.AbyssalTotals => (360, 200),
        WidgetPreset.LastKillmail => (360, 140),
        WidgetPreset.KillAlert => (480, 140),
        WidgetPreset.FleetDps => (360, 240),
        _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, null)
    };

    private static WidgetConfig _Preset(WidgetPreset preset, string name, string[] fields,
        Dictionary<string, string>? options = null) => new()
    {
        Id = Key(preset),
        Preset = preset,
        Name = name,
        Fields = fields,
        Options = options ?? []
    };
}
