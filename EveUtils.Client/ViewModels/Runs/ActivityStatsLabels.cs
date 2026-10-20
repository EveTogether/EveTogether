using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using EveUtils.Client.Opsec;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Shared.Modules.Runs.Earnings;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Sde.Dtos;
using Material.Icons;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>An activity as the BY ACTIVITY list names it (ET-517).</summary>
/// <param name="NameText">The site's name, marked as a location for OPSEC (ET-417).</param>
/// <param name="SearchText">Name, family and type unmarked, for the search ET-518 adds: it matches on what is typed, never
/// on what is masked.</param>
/// <param name="FamilyText">Faction or archetype and DED rating from the static data, empty where it has none.</param>
public sealed record ActivityStatsLabel(string NameText, string SearchText, string FamilyText, string TypeText, RunTypeId TypeId,
    MaterialIconKind TypeIcon);

/// <summary>
/// Names an <see cref="ActivityStatsKey"/>: the pilot's own latest name for it when they flew it, otherwise what the
/// static data calls it — an activity only a server knows has no run of the pilot's to take a name from. Without the SDE
/// the name falls back to the key's own normalised one.
/// </summary>
public sealed class ActivityStatsLabels(ISdeAccessor? sde, RunRowFacts facts)
{
    private readonly Lazy<IReadOnlyDictionary<int, SdeSite>> _sites = new(() => sde is { IsAvailable: true } available
        ? available.SearchSites().GroupBy(site => site.DungeonId).ToDictionary(group => group.Key, group => group.First())
        : new Dictionary<int, SdeSite>());

    public ActivityStatsLabel For(ActivityStatsKey key, string? siteName)
    {
        ActivityKind kind = key.Kind switch
        {
            ActivityStatsKind.Mission => ActivityKind.Mission,
            ActivityStatsKind.Abyssal => ActivityKind.Abyssal,
            _ => ActivityKind.Site
        };
        SdeSite? site = key.Kind is ActivityStatsKind.Site or ActivityStatsKind.Homefront && key.TypeId > 0
            ? _sites.Value.GetValueOrDefault(key.TypeId)
            : null;
        string name = _NameOf(key, siteName, site);
        RunTypeDefinition type = facts.TypeOf(kind, null, key.TypeId, name);
        string family = _FamilyOf(key, site);
        return new ActivityStatsLabel(OpsecText.Mark(name), $"{name} {family} {type.Name}", family, type.Name, type.Id, type.Icon);
    }

    private string _NameOf(ActivityStatsKey key, string? siteName, SdeSite? site)
    {
        if (!string.IsNullOrWhiteSpace(siteName))
            return siteName;

        if (site is not null)
            return site.Name;

        return key.Kind switch
        {
            ActivityStatsKind.Abyssal => AbyssalFilamentName.From(key.AbyssalTier, key.AbyssalWeather),
            ActivityStatsKind.Mission when key.TypeId > 0 && sde?.GetMission(key.TypeId) is { } mission => mission.Name,
            _ => key.Name is { Length: > 0 } named ? CultureInfo.InvariantCulture.TextInfo.ToTitleCase(named) : "Unnamed site"
        };
    }

    private static string _FamilyOf(ActivityStatsKey key, SdeSite? site)
    {
        if (key.Kind is ActivityStatsKind.Mission)
            return key.MissionLevel is { } level ? $"Level {level.ToString(CultureInfo.InvariantCulture)}" : string.Empty;

        if (site is null)
            return string.Empty;

        List<string> parts = [.. new[] { site.FactionName, site.ArchetypeName }
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        if (site.DedRating is { } ded && ded > 0)
            parts.Add($"DED {ded.ToString(CultureInfo.InvariantCulture)}");
        return string.Join(" · ", parts);
    }
}
