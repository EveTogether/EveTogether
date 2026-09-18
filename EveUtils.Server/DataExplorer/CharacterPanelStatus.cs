namespace EveUtils.Server.DataExplorer;

/// <summary>A paired character's state, split the same way as the Dashboard's character tile.</summary>
public enum CharacterPanelStatus
{
    Live = 0,
    Idle = 1,
    NeverConnected = 2,
    Failing = 3,
}
