using System;
using System.Globalization;
using EveUtils.Shared.Modules.Skills;

namespace EveUtils.Client.ViewModels.FitBrowser;

/// <summary>Formats a skill-training estimate (SP + Omega time) for the "Skills Required" panel — shared by the
/// per-skill rows and the panel total, so both read the same ("210.7k SP · 4d 4h 21m").</summary>
public static class SkillEstimateFormat
{
    public static string SpAndTime(double skillPoints, TimeSpan time) =>
        $"{Sp(skillPoints)} · {EveDurationFormatter.Format(time)}";

    public static string Sp(double sp) => sp switch
    {
        >= 1_000_000 => $"{(sp / 1_000_000).ToString("0.#", CultureInfo.InvariantCulture)}M SP",
        >= 1_000 => $"{(sp / 1_000).ToString("0.#", CultureInfo.InvariantCulture)}k SP",
        _ => $"{sp.ToString("0", CultureInfo.InvariantCulture)} SP"
    };
}
