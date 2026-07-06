namespace EveUtils.Shared.Modules.Fleet.Entities;

/// <summary>Who can see and join a fleet on a server.</summary>
public enum FleetVisibility
{
    /// <summary>Only characters the creator explicitly invites can join.</summary>
    InviteOnly = 0,

    /// <summary>Any character connected to the server can list and join the fleet.</summary>
    Public = 1
}
