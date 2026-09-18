using EveUtils.Shared.Modules.Fleet.Entities;

namespace EveUtils.Server.DataExplorer;

public static class FleetRoleText
{
    public static string Label(FleetRole role) => role switch
    {
        FleetRole.FleetCommander => "Fleet commander",
        FleetRole.WingCommander => "Wing commander",
        FleetRole.SquadCommander => "Squad commander",
        FleetRole.SquadMember => "Member",
        _ => "No position",
    };
}
