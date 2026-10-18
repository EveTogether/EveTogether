using System.Globalization;
using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Earnings;

/// <summary>
/// Which runs count as the same activity for the earnings statistic (ET-513): one site, one mission at one level, one
/// abyssal tier and weather. Not <c>GroupCode ?? Id</c>, which is one flown session, and not <c>RunTypeId</c>, which
/// is derived again at every read and can move after a catalogue update. A site's id is its canonical twin's, so both
/// halves of a twin pair land here as one.
/// </summary>
public sealed record ActivityStatsKey
{
    public required ActivityStatsKind Kind { get; init; }

    /// <summary>Which id space <see cref="TypeId"/> is from; set on <see cref="ActivityStatsKind.Site"/> only.</summary>
    public SiteTypeSource? SiteTypeSource { get; init; }

    /// <summary>The canonical dungeon id of a site or homefront, or the mission id; 0 when keyed on <see cref="Name"/>.</summary>
    public int TypeId { get; init; }

    /// <summary>The normalised site or mission name, set only when there is no id to key on.</summary>
    public string? Name { get; init; }

    public int? MissionLevel { get; init; }
    public int? AbyssalTier { get; init; }
    public string? AbyssalWeather { get; init; }

    /// <summary>The key as one stable string, for a dictionary, a table column or the wire (ET-514).</summary>
    public string Code => Kind switch
    {
        ActivityStatsKind.Site => $"site:{SiteTypeSource}:{_IdOrName()}",
        ActivityStatsKind.Mission => $"mission:{_IdOrName()}:{MissionLevel?.ToString(CultureInfo.InvariantCulture)}",
        ActivityStatsKind.Abyssal => $"abyssal:{AbyssalTier?.ToString(CultureInfo.InvariantCulture)}:{AbyssalWeather}",
        _ => $"homefront:{TypeId.ToString(CultureInfo.InvariantCulture)}"
    };

    public static ActivityStatsKey Site(SiteTypeSource source, int canonicalDungeonId) =>
        new() { Kind = ActivityStatsKind.Site, SiteTypeSource = source, TypeId = canonicalDungeonId };

    public static ActivityStatsKey SiteNamed(SiteTypeSource source, string normalisedName) =>
        new() { Kind = ActivityStatsKind.Site, SiteTypeSource = source, Name = normalisedName };

    public static ActivityStatsKey Mission(int missionId, int? level) =>
        new() { Kind = ActivityStatsKind.Mission, TypeId = missionId, MissionLevel = level };

    public static ActivityStatsKey MissionNamed(string normalisedName, int? level) =>
        new() { Kind = ActivityStatsKind.Mission, Name = normalisedName, MissionLevel = level };

    public static ActivityStatsKey Abyssal(int tier, string weather) =>
        new() { Kind = ActivityStatsKind.Abyssal, AbyssalTier = tier, AbyssalWeather = weather };

    public static ActivityStatsKey Homefront(int dungeonId) =>
        new() { Kind = ActivityStatsKind.Homefront, TypeId = dungeonId };

    private string _IdOrName() => Name is null ? TypeId.ToString(CultureInfo.InvariantCulture) : $"name:{Name}";
}
