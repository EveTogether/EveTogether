using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using EveUtils.Shared.Modules.Skills;

namespace EveUtils.Client.ViewModels.FitBrowser;

/// <summary>One selected stat's value for a skill-impact row, at the skill's next level and at level V, against the
/// value at the character's trained levels — the row shows the change, never the bare value.</summary>
public sealed record SkillImpactStatGain(SkillImpactStat Stat, string Label, double Base, double AtNextLevel, double AtFive)
{
    public string NextText => $"{Label} {SkillImpactStats.Delta(Stat, Base, AtNextLevel)}";
    public string FiveText => $"{Label} {SkillImpactStats.Delta(Stat, Base, AtFive)}";

    /// <summary>"-0.61 s align time", the next level's change as a sentence says it.</summary>
    public string NextSentenceText =>
        $"{SkillImpactStats.Delta(Stat, Base, AtNextLevel)} {SkillImpactStats.All.First(meta => meta.Stat == Stat).RuleName}";
}

/// <summary>The fifteen SKILL IMPACT stats as the screen names and writes them (mockup v5): the fit-detail section
/// each sits in, the chip label, the stat-menu label, the lower-case name a sentence uses, and the value with its unit.</summary>
public static class SkillImpactStats
{
    public sealed record Meta(SkillImpactStat Stat, string Group, string Label, string MenuLabel, string RuleName);

    public static readonly IReadOnlyList<Meta> All =
    [
        new(SkillImpactStat.Dps, "OFFENSE", "DPS", "DPS (guns, missiles)", "DPS"),
        new(SkillImpactStat.DroneDps, "OFFENSE", "Drone DPS", "Drone DPS", "drone DPS"),
        new(SkillImpactStat.Optimal, "OFFENSE", "Optimal", "Optimal", "optimal"),
        new(SkillImpactStat.Falloff, "OFFENSE", "Falloff", "Falloff", "falloff"),
        new(SkillImpactStat.Tracking, "OFFENSE", "Tracking", "Tracking", "tracking"),
        new(SkillImpactStat.Ehp, "TANK", "EHP", "EHP", "EHP"),
        new(SkillImpactStat.Capacitor, "CAPACITOR", "Cap stability", "Cap stability", "cap stability"),
        new(SkillImpactStat.Speed, "NAVIGATION", "Speed", "Speed (propmod on)", "speed"),
        new(SkillImpactStat.AlignTime, "NAVIGATION", "Align time", "Align time", "align time"),
        new(SkillImpactStat.Signature, "NAVIGATION", "Signature", "Signature (propmod on)", "signature"),
        new(SkillImpactStat.LockRange, "TARGETING", "Lock range", "Lock range", "lock range"),
        new(SkillImpactStat.ScanResolution, "TARGETING", "Scan resolution", "Scan resolution", "scan resolution"),
        new(SkillImpactStat.SensorStrength, "TARGETING", "Scan strength", "Scan strength", "scan strength"),
        new(SkillImpactStat.FreeCpu, "FITTING", "CPU free", "CPU free", "CPU"),
        new(SkillImpactStat.FreePg, "FITTING", "Power grid free", "Power grid free", "power grid"),
    ];

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // The scanner encodes capacitor as seconds-until-empty (capped at 3600) or 3600 + the stable percentage.
    private const double CapStableOffset = 3600;

    public static string Value(SkillImpactStat stat, double value) => stat switch
    {
        SkillImpactStat.Dps or SkillImpactStat.DroneDps => value.ToString("N0", Inv),
        SkillImpactStat.Optimal or SkillImpactStat.Falloff or SkillImpactStat.LockRange => $"{(value / 1000).ToString("0.0#", Inv)} km",
        SkillImpactStat.Tracking => $"{value.ToString("0.####", Inv)} rad/s",
        SkillImpactStat.Ehp => $"{value.ToString("N0", Inv)} hp",
        SkillImpactStat.Capacitor => value > CapStableOffset
            ? $"stable {(value - CapStableOffset).ToString("0", Inv)}%"
            : $"lasts {(int)(value / 60)}m {(int)(value % 60)}s",
        SkillImpactStat.Speed => $"{value.ToString("N0", Inv)} m/s",
        SkillImpactStat.AlignTime => $"{value.ToString("0.00", Inv)} s",
        SkillImpactStat.Signature => $"{value.ToString("0", Inv)} m",
        SkillImpactStat.ScanResolution => $"{value.ToString("0", Inv)} mm",
        SkillImpactStat.SensorStrength => $"{value.ToString("0.#", Inv)} pt",
        SkillImpactStat.FreeCpu => $"{value.ToString("+0.0;-0.0;0.0", Inv)} tf",
        SkillImpactStat.FreePg => $"{value.ToString("+0.#;-0.#;0", Inv)} MW",
        _ => value.ToString("0.##", Inv),
    };

    public static string Delta(SkillImpactStat stat, double from, double to)
    {
        double delta = to - from;
        return stat switch
        {
            SkillImpactStat.Dps or SkillImpactStat.DroneDps => delta.ToString("+#,0;-#,0;0", Inv),
            SkillImpactStat.Optimal or SkillImpactStat.Falloff or SkillImpactStat.LockRange => $"{(delta / 1000).ToString("+0.0#;-0.0#;0", Inv)} km",
            SkillImpactStat.Tracking => $"{delta.ToString("+0.####;-0.####;0", Inv)} rad/s",
            SkillImpactStat.Ehp => $"{delta.ToString("+#,0;-#,0;0", Inv)} hp",
            SkillImpactStat.Capacitor => from > CapStableOffset || to > CapStableOffset
                ? (from > CapStableOffset && to > CapStableOffset ? $"{delta.ToString("+0;-0;0", Inv)}% stable" : $"to {Value(stat, to)}")
                : $"{delta.ToString("+0;-0;0", Inv)} s",
            SkillImpactStat.Speed => $"{delta.ToString("+#,0;-#,0;0", Inv)} m/s",
            SkillImpactStat.AlignTime => $"{delta.ToString("+0.00;-0.00;0", Inv)} s",
            SkillImpactStat.Signature => $"{delta.ToString("+0.#;-0.#;0", Inv)} m",
            SkillImpactStat.ScanResolution => $"{delta.ToString("+0;-0;0", Inv)} mm",
            SkillImpactStat.SensorStrength => $"{delta.ToString("+0.#;-0.#;0", Inv)} pt",
            SkillImpactStat.FreeCpu => $"{delta.ToString("+0.0;-0.0;0", Inv)} tf",
            SkillImpactStat.FreePg => $"{delta.ToString("+0.#;-0.#;0", Inv)} MW",
            _ => delta.ToString("+0.##;-0.##;0", Inv),
        };
    }

    /// <summary>"DPS, align time or CPU" — the chosen stats as a sentence names them.</summary>
    public static string RuleList(IReadOnlyList<string> names) => names.Count switch
    {
        0 => "",
        1 => names[0],
        _ => $"{string.Join(", ", names.Take(names.Count - 1))} or {names[^1]}",
    };

    public static string UnsignedAmount(SkillImpactStat stat, double value) => Value(stat, Math.Abs(value)).TrimStart('+');
}
