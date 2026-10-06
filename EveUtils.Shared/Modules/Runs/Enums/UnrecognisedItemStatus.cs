namespace EveUtils.Shared.Modules.Runs.Enums;

public enum UnrecognisedItemStatus
{
    /// <summary>No SDE type carries the name yet.</summary>
    Open,

    /// <summary>A later SDE knew the name. The row stays as the record of what was recognised and when.</summary>
    Resolved
}
