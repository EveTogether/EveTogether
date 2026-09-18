namespace EveUtils.Server.DataExplorer.Destructive;

public enum ConfirmTier
{
    /// <summary>Arm the action, read its consequences inline, confirm.</summary>
    TwoStep,

    /// <summary>The confirm button stays disabled until the record's name is typed exactly.</summary>
    TypeName,
}
