namespace EveUtils.Shared.Modules.Fleet.Cleanup;

/// <summary>What the cleanup sweep should do with a fleet this pass.</summary>
public enum FleetCleanupAction
{
    /// <summary>Leave it as-is.</summary>
    None = 0,

    /// <summary>Soft-delete: an inactive Active fleet → <see cref="Entities.FleetState.Archived"/>.</summary>
    Archive = 1,

    /// <summary>Hard-delete: a long-archived fleet → removed (rows cascade).</summary>
    Delete = 2,
}
