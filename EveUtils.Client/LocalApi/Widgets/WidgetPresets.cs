using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace EveUtils.Client.LocalApi.Widgets;

/// <summary>
/// The built-in presets: their defaults plus what the manager offers on them (fields with their data source, options,
/// base size). They live in code only and are never stored, so they cannot be changed: customizing one saves a copy
/// under a new id. Location fields are off in every preset.
/// </summary>
public static class WidgetPresets
{
    public const string DefaultAccent = "#4fc3f7";

    private static readonly WidgetOptionDefinition Period = new("period", "Period",
        [new("session", "Session"), new("today", "Today"), new("week", "Week"), new("month", "Month")]);

    private static readonly WidgetFieldDefinition CurrentSystem = new("system", "Current system", WidgetFieldSource.Api, IsLocation: true);

    public static IReadOnlyList<WidgetPresetDefinition> Definitions { get; } =
    [
        _Define(WidgetPreset.LiveDps, "Live DPS", WidgetGroup.Combat, 320, 200,
            "Your DPS out and in with the peak, live while you fight.",
            [
                new("name", "Character name", WidgetFieldSource.Api), new("dpsOut", "DPS out", WidgetFieldSource.Api),
                new("dpsIn", "DPS in", WidgetFieldSource.Api), new("peak", "Peak DPS", WidgetFieldSource.Api),
                new("reps", "Reps in / out", WidgetFieldSource.Api),
                new("application", "Application (sweet spot / adjust)", WidgetFieldSource.Api),
                new("bounty", "Bounty this session", WidgetFieldSource.Api), CurrentSystem
            ],
            ["name", "dpsOut", "dpsIn", "peak"], []),
        _Define(WidgetPreset.DpsGraph, "DPS graph", WidgetGroup.Combat, 480, 210,
            "The scrolling graph of the DPS pop-out: out, in and reps, optionally neut and cap.",
            [
                new("name", "Character name", WidgetFieldSource.Api), new("dpsOut", "DPS out line", WidgetFieldSource.Api),
                new("dpsIn", "DPS in line", WidgetFieldSource.Api), new("reps", "Reps line", WidgetFieldSource.Api),
                new("numbers", "Figures under the graph", WidgetFieldSource.Api),
                new("neut", "Neut lane", WidgetFieldSource.Api), new("cap", "Cap lane", WidgetFieldSource.Api), CurrentSystem
            ],
            ["name", "dpsOut", "dpsIn", "reps", "numbers"],
            [new("span", "Time span", [new("60", "60 s"), new("120", "2 min"), new("300", "5 min")])]),
        _Define(WidgetPreset.CurrentRun, "Current run", WidgetGroup.Runs, 360, 210,
            "The run you are flying now: what it is, the clock and what it has made so far.",
            [
                new("type", "Run type", WidgetFieldSource.Api), new("site", "Site name", WidgetFieldSource.Api),
                new("tier", "Abyssal tier and weather", WidgetFieldSource.Api), new("clock", "Clock", WidgetFieldSource.Api),
                new("loot", "Loot so far", WidgetFieldSource.Api), new("bounty", "Bounty", WidgetFieldSource.Api),
                new("total", "Run total", WidgetFieldSource.Api), new("kills", "Kills", WidgetFieldSource.Api),
                new("crew", "Crew", WidgetFieldSource.Api),
                new("signature", "Signature id", WidgetFieldSource.Api, IsLocation: true), CurrentSystem
            ],
            ["type", "site", "tier", "clock", "loot", "bounty", "total", "kills"], []),
        _Define(WidgetPreset.RunTotals, "Run totals", WidgetGroup.Runs, 360, 230,
            "Runs, ISK and ISK per hour for this session, today, this week or this month.",
            [
                new("runs", "Runs", WidgetFieldSource.Api), new("isk", "Total ISK", WidgetFieldSource.Api),
                new("iskPerHour", "ISK per hour", WidgetFieldSource.Api), new("flownTime", "Time flown", WidgetFieldSource.Api),
                new("bestDrop", "Best drop", WidgetFieldSource.Api), new("averagePerRun", "Average per run", WidgetFieldSource.Api),
                new("perCharacter", "Per character", WidgetFieldSource.Api)
            ],
            ["runs", "isk", "iskPerHour", "flownTime", "bestDrop"],
            [
                Period,
                new("kind", "Runs", [
                    new("all", "All"), new("abyssal", "Abyssal"), new("combat", "Combat sites"),
                    new("mission", "Missions"), new("mining", "Mining")
                ])
            ]),
        _Define(WidgetPreset.AbyssalTotals, "Abyssal totals", WidgetGroup.Runs, 360, 230,
            "Abyssal clears, ISK per hour, average clear time and a split per tier and weather.",
            [
                new("runs", "Clears", WidgetFieldSource.Api), new("isk", "Total ISK", WidgetFieldSource.Api),
                new("iskPerHour", "ISK per hour", WidgetFieldSource.Api),
                new("averageClear", "Average clear time", WidgetFieldSource.Api),
                new("byTier", "Per tier and weather", WidgetFieldSource.Api),
                new("filaments", "Filaments used", WidgetFieldSource.App), new("shipsLost", "Ships lost", WidgetFieldSource.Api)
            ],
            ["runs", "isk", "iskPerHour", "averageClear", "byTier"], [Period]),
        _Define(WidgetPreset.LastKillmail, "Last killmail", WidgetGroup.Killmails, 360, 140,
            "Your latest kill or loss, valued like the KILLMAILS screen.",
            [
                new("badge", "Kill / loss badge", WidgetFieldSource.Api), new("ship", "Ship", WidgetFieldSource.Api),
                new("isk", "ISK value", WidgetFieldSource.Api), new("attackers", "Attackers", WidgetFieldSource.Api),
                new("ago", "Time ago", WidgetFieldSource.Api), new("run", "Linked run", WidgetFieldSource.Api),
                new("victim", "Victim name", WidgetFieldSource.Api), new("finalBlow", "Final blow", WidgetFieldSource.Api),
                new("corporation", "Corporation", WidgetFieldSource.Api), CurrentSystem
            ],
            ["badge", "ship", "isk", "attackers", "ago", "run", "victim"],
            [new("show", "Show", [new("both", "Kills and losses"), new("kills", "Kills only"), new("losses", "Losses only")])]),
        _Define(WidgetPreset.KillAlert, "Kill alert", WidgetGroup.Killmails, 480, 120,
            "Pops up for a few seconds on a new kill or loss, never for old mails.",
            [
                new("ship", "Ship", WidgetFieldSource.Api), new("victim", "Victim name", WidgetFieldSource.Api),
                new("isk", "ISK value", WidgetFieldSource.Api), CurrentSystem
            ],
            ["ship", "victim", "isk"],
            [
                new("holdSeconds", "Visible for", [new("5", "5 s"), new("8", "8 s"), new("15", "15 s")], Default: "8"),
                new("minIsk", "Minimum ISK", [])
            ]),
        _Define(WidgetPreset.FleetDps, "Fleet DPS", WidgetGroup.Fleet, 360, 240,
            "DPS per fleet member and the fleet total. Only members who share their DPS show up.",
            [
                new("names", "Member names", WidgetFieldSource.Api), new("dpsOut", "DPS out", WidgetFieldSource.Api),
                new("dpsIn", "DPS in", WidgetFieldSource.Api), new("total", "Fleet total", WidgetFieldSource.Api),
                new("systems", "Members' systems", WidgetFieldSource.Api, IsLocation: true)
            ],
            ["names", "dpsOut", "dpsIn", "total"], [])
    ];

    public static IReadOnlyList<WidgetConfig> All { get; } = [.. Definitions.Select(definition => definition.Defaults)];

    /// <summary>The preset's fixed id and URL segment, e.g. <c>live-dps</c>.</summary>
    public static string Key(WidgetPreset preset) => JsonSerializer.Serialize(preset).Trim('"');

    public static WidgetConfig? Find(string id) => All.FirstOrDefault(preset => preset.Id == id);

    public static bool IsBuiltIn(string id) => Find(id) is not null;

    public static WidgetPresetDefinition Definition(WidgetPreset preset) =>
        Definitions.FirstOrDefault(definition => definition.Defaults.Preset == preset)
        ?? throw new ArgumentOutOfRangeException(nameof(preset), preset, null);

    /// <summary>Width × height in pixels at 100 % scale — what OBS's browser source needs. Measured on the widget page
    /// with every field on, so OBS never crops one; the page draws at this width.</summary>
    public static (int Width, int Height) BaseSize(WidgetPreset preset)
    {
        var definition = Definition(preset);
        return (definition.BaseWidth, definition.BaseHeight);
    }

    /// <summary>The widget's width × height at 100 % scale: its preset's size, or the bottom bar for the ticker theme.</summary>
    public static (int Width, int Height) Size(WidgetConfig config) =>
        config.Theme == WidgetTheme.Ticker ? (900, 40) : BaseSize(config.Preset);

    private static WidgetPresetDefinition _Define(WidgetPreset preset, string name, WidgetGroup group, int width, int height,
        string description, WidgetFieldDefinition[] fields, string[] defaultFields, WidgetOptionDefinition[] options) =>
        new(new WidgetConfig
            {
                Id = Key(preset),
                Preset = preset,
                Name = name,
                Fields = defaultFields,
                Options = options.ToDictionary(option => option.Key, option => option.DefaultValue)
            },
            group, description, width, height, fields, options);
}
