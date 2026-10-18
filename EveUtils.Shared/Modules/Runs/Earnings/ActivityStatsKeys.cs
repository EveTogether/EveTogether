using System.Text.RegularExpressions;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Sde.Dtos;

namespace EveUtils.Shared.Modules.Runs.Earnings;

/// <summary>The <see cref="ActivityStatsKey"/> a stored run counts under (ET-513), read off the run's own
/// <see cref="Run.ActivityKind"/> and <see cref="Run.SiteTypeSource"/>.</summary>
internal static partial class ActivityStatsKeys
{
    /// <param name="sitesByName">The catalogue's exact-name lookup (<see cref="ISdeAccessor.FindSitesByExactName"/>),
    /// empty when the SDE is not there: the twin a run's id or name belongs to is decided by it.</param>
    public static ActivityStatsKey? For(Run run, IEnumerable<RunParameter> parameters,
        Func<string, IReadOnlyList<SdeSite>> sitesByName) =>
        run.ActivityKind switch
        {
            ActivityKind.Abyssal => AbyssalPocket.Parse(parameters
                    .FirstOrDefault(parameter => parameter.ParameterKey == RunParameterKey.AbyssalFilament)?.TypedValue)
                is var (tier, weather)
                ? ActivityStatsKey.Abyssal(tier, weather)
                : null,
            ActivityKind.Mission => run.SiteTypeId > 0
                ? ActivityStatsKey.Mission(run.SiteTypeId, run.MissionLevel)
                : NormaliseName(run.SiteName) is { } missionName
                    ? ActivityStatsKey.MissionNamed(missionName, run.MissionLevel)
                    : null,
            ActivityKind.Site => _Site(run, sitesByName),
            _ => null
        };

    /// <summary>A name as the key compares it: case-insensitive, outer spaces trimmed and inner runs of white space
    /// read as one. Null when nothing is left.</summary>
    public static string? NormaliseName(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : _WhiteSpace().Replace(name.Trim(), " ").ToLowerInvariant();

    private static ActivityStatsKey? _Site(Run run, Func<string, IReadOnlyList<SdeSite>> sitesByName)
    {
        string? name = NormaliseName(run.SiteName);
        if (run.SiteTypeSource is SiteTypeSource.Uncatalogued)
            return name is null ? null : ActivityStatsKey.SiteNamed(SiteTypeSource.Uncatalogued, name);

        if (HomefrontCatalogue.IsHomefrontDungeonId(run.SiteTypeId))
            return ActivityStatsKey.Homefront(run.SiteTypeId);

        int dungeonId = _CanonicalDungeonId(run.SiteTypeId, run.SiteName, sitesByName);
        if (dungeonId > 0)
            return ActivityStatsKey.Site(run.SiteTypeSource, dungeonId);

        return name is null ? null : ActivityStatsKey.SiteNamed(run.SiteTypeSource, name);
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
