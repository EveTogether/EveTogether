using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Client.Runs;

/// <summary>Which runs a total counts, by the TYPE they show under (ET-226). Data, relic, wormhole and an unknown site
/// only count under <see cref="All"/>.</summary>
public enum RunsKind
{
    All,
    Abyssal,
    Combat,
    Mission,
    Mining
}

public static class RunsKinds
{
    public static bool Matches(this RunsKind kind, RunTypeId type) => kind switch
    {
        RunsKind.All => true,
        RunsKind.Abyssal => type is RunTypeId.Abyssal,
        RunsKind.Combat => type is RunTypeId.CombatSite or RunTypeId.Homefront,
        RunsKind.Mission => type is RunTypeId.Mission,
        RunsKind.Mining => type is RunTypeId.Mining or RunTypeId.OreSite or RunTypeId.GasSite,
        _ => false
    };
}
