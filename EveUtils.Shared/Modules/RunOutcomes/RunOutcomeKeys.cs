using EveUtils.Shared.Modules.RunOutcomes.Entities;
using EveUtils.Shared.Modules.Runs.Earnings;
using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.RunOutcomes;

/// <summary>An <see cref="ActivityStatsKey"/> as the server takes it from the wire and keeps it on a
/// <see cref="RunOutcome"/>. A name is normalised here with the same function the client keys its own runs by, so an
/// uncatalogued site groups the same on both sides.</summary>
internal static class RunOutcomeKeys
{
    private const int MaximumNameLength = 255;
    private const int MaximumWeatherLength = 32;

    /// <summary>The key with only the parts its kind uses, the name normalised; null for a key that names no
    /// activity.</summary>
    public static ActivityStatsKey? Normalised(ActivityStatsKey key)
    {
        string? name = ActivityStatsKeys.NormaliseName(key.Name);
        if (name is { Length: > MaximumNameLength })
            return null;

        return key.Kind switch
        {
            ActivityStatsKind.Site when key.SiteTypeSource is { } source && Enum.IsDefined(source) => key.TypeId > 0
                ? ActivityStatsKey.Site(source, key.TypeId)
                : name is null ? null : ActivityStatsKey.SiteNamed(source, name),
            ActivityStatsKind.Mission => key.TypeId > 0
                ? ActivityStatsKey.Mission(key.TypeId, key.MissionLevel)
                : name is null ? null : ActivityStatsKey.MissionNamed(name, key.MissionLevel),
            ActivityStatsKind.Abyssal when key is { AbyssalTier: { } tier, AbyssalWeather: { Length: > 0 and <= MaximumWeatherLength } weather }
                => ActivityStatsKey.Abyssal(tier, weather),
            ActivityStatsKind.Homefront when key.TypeId > 0 => ActivityStatsKey.Homefront(key.TypeId),
            _ => null
        };
    }

    public static void Apply(RunOutcome outcome, ActivityStatsKey key)
    {
        outcome.KeyCode = key.Code;
        outcome.StatsKind = key.Kind;
        outcome.SiteTypeSource = key.SiteTypeSource;
        outcome.TypeId = key.TypeId;
        outcome.NormalizedName = key.Name;
        outcome.MissionLevel = key.MissionLevel;
        outcome.AbyssalTier = key.AbyssalTier;
        outcome.AbyssalWeather = key.AbyssalWeather;
    }

    public static ActivityStatsKey Of(RunOutcome outcome) => new()
    {
        Kind = outcome.StatsKind,
        SiteTypeSource = outcome.SiteTypeSource,
        TypeId = outcome.TypeId,
        Name = outcome.NormalizedName,
        MissionLevel = outcome.MissionLevel,
        AbyssalTier = outcome.AbyssalTier,
        AbyssalWeather = outcome.AbyssalWeather
    };
}
