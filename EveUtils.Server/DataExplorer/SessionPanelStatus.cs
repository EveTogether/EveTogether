namespace EveUtils.Server.DataExplorer;

/// <summary>A session as the panel reads it from its last heartbeat, in the order the list groups by.</summary>
public enum SessionPanelStatus
{
    Live = 0,
    Idle = 1,
    Lapsing = 2,
}
