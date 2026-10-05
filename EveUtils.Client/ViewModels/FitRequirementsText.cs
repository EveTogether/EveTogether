using System;
using System.Collections.Generic;
using System.Linq;
using EveUtils.Client.Skills;

namespace EveUtils.Client.ViewModels;

/// <summary>"19 skills, e.g. Medium Hybrid Turret V, Gunnery IV …": what a fit already requires, highest levels first.</summary>
internal static class FitRequirementsText
{
    private const int Examples = 5;

    public static string Format(IReadOnlyDictionary<int, int> fitLevels, Func<int, string> name)
    {
        if (fitLevels.Count == 0)
        {
            return "The fit's skills could not be read.";
        }
        IEnumerable<string> examples = fitLevels
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => name(pair.Key), StringComparer.OrdinalIgnoreCase)
            .Take(Examples)
            .Select(pair => $"{name(pair.Key)} {RomanLevel.Text(pair.Value)}");
        return $"{fitLevels.Count} skills, e.g. {string.Join(", ", examples)}{(fitLevels.Count > Examples ? " …" : "")}";
    }
}
