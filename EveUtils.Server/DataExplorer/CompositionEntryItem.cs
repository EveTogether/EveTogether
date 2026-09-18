using EveUtils.Shared.Modules.Fleet.Composition;

namespace EveUtils.Server.DataExplorer;

public sealed class CompositionEntryItem
{
    public required FleetCompositionEntry Entry { get; init; }

    /// <summary>The shared fit the entry was taken from, when it still exists.</summary>
    public int? LinkedSharedFitId { get; init; }

    /// <summary>The entry names a shared fit that is gone. It keeps its own copy, so only the link is broken.</summary>
    public bool IsLinkBroken => Entry.Fit.ServerSharedFitId is not null && LinkedSharedFitId is null;
}
