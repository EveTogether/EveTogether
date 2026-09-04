using EveUtils.Shared.Modules.Fleet.Entities;

namespace EveUtils.Client.ViewModels.Fleets;

/// <summary>
/// The three bands of the fleet overview (ET-170), read straight off <see cref="FleetActivation"/>: a started fleet
/// is active, a fleet that stands ready with its roster is standing by, a concluded one is finished. The names are
/// the screen's, the states are the domain's — there is no fourth state and no screen-only one.
/// </summary>
public enum FleetOverviewGroup
{
    Active,
    StandingBy,
    Finished,
}

public static class FleetOverviewGroupExtensions
{
    public static FleetOverviewGroup ToGroup(this FleetActivation activation) => activation switch
    {
        FleetActivation.Active => FleetOverviewGroup.Active,
        FleetActivation.Concluded => FleetOverviewGroup.Finished,
        _ => FleetOverviewGroup.StandingBy,
    };
}
