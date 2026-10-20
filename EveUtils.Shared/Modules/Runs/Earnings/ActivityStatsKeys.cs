using System.Text.RegularExpressions;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Sde.Dtos;

namespace EveUtils.Shared.Modules.Runs.Earnings;

/// <summary>The <see cref="ActivityStatsKey"/> a stored run counts under (ET-513), read off the run's own
/// <see cref="Run.ActivityKind"/> and <see cref="Run.SiteTypeSource"/>.</summary>
public static partial class ActivityStatsKeys
{
    /// <param name="sitesByName">The catalogue's exact-name lookup (<see cref="ISdeAccessor.FindSitesByExactName"/>),
    /// empty when the SDE is not there: the twin a run's id or name belongs to is decided by it.</param>
    public static ActivityStatsKey? For(Run run, IEnumerable<RunParameter> parameters,
        Func<string, IReadOnlyList<SdeSite>> sitesByName) =>
        For(run.ActivityKind, run.SiteTypeSource, run.SiteTypeId, run.SiteName, run.MissionLevel,
            run.ActivityKind is ActivityKind.Abyssal
                ? AbyssalPocket.Parse(parameters
                    .FirstOrDefault(parameter => parameter.ParameterKey == RunParameterKey.AbyssalFilament)?.TypedValue)
                : null,
            sitesByName);

    /// <summary>The key from the facts a run window holds before any run is stored (ET-516): the same rule as for a
    /// stored run, so the panel at a site's start reads the figures its earlier runs were filed under.</summary>
    public static ActivityStatsKey? For(ActivityKind kind, SiteTypeSource siteTypeSource, int siteTypeId, string? siteName,
        int? missionLevel, (int Tier, string Weather)? abyssal, Func<string, IReadOnlyList<SdeSite>> sitesByName) =>
        kind switch
        {
            ActivityKind.Abyssal => abyssal is var (tier, weather) ? ActivityStatsKey.Abyssal(tier, weather) : null,
            ActivityKind.Mission => siteTypeId > 0
                ? ActivityStatsKey.Mission(siteTypeId, missionLevel)
                : NormaliseName(siteName) is { } missionName
                    ? ActivityStatsKey.MissionNamed(missionName, missionLevel)
                    : null,
            ActivityKind.Site => _Site(siteTypeSource, siteTypeId, siteName, sitesByName),
            _ => null
        };

    /// <summary>A name as the key compares it: case-insensitive, outer spaces trimmed and inner runs of white space
    /// read as one. Null when nothing is left.</summary>
    public static string? NormaliseName(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : _WhiteSpace().Replace(name.Trim(), " ").ToLowerInvariant();

    private static ActivityStatsKey? _Site(SiteTypeSource siteTypeSource, int siteTypeId, string? siteName,
        Func<string, IReadOnlyList<SdeSite>> sitesByName)
    {
        string? name = NormaliseName(siteName);
        if (siteTypeSource is SiteTypeSource.Uncatalogued)
            return name is null ? null : ActivityStatsKey.SiteNamed(SiteTypeSource.Uncatalogued, name);

        if (HomefrontCatalogue.IsHomefrontDungeonId(siteTypeId))
            return ActivityStatsKey.Homefront(siteTypeId);

        int dungeonId = _CanonicalDungeonId(siteTypeId, siteName, sitesByName);
        if (dungeonId > 0)
            return ActivityStatsKey.Site(siteTypeSource, dungeonId);

        return name is null ? null : ActivityStatsKey.SiteNamed(siteTypeSource, name);
    }

    // A run started from a copied name that matched both halves of a twin pair was saved with id 0 (ET-228); the twins
    // collapse to one row, so it still finds the id a pick from the catalogue carries.
    private static int _CanonicalDungeonId(int siteTypeId, string? siteName, Func<string, IReadOnlyList<SdeSite>> sitesByName)
    {
        if (string.IsNullOrWhiteSpace(siteName))
            return siteTypeId;

        IReadOnlyList<SdeSite> named = sitesByName(siteName);
        if (siteTypeId > 0)
            return SdeSiteCanonicalization.CanonicalDungeonId(named, siteTypeId);

        return SdeSiteCanonicalization.Canonicalize(named) is [{ DungeonId: var only }] ? only : 0;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex _WhiteSpace();
}
