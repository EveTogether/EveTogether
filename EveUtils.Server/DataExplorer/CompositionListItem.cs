using EveUtils.Shared.Modules.Fleet.Composition;

namespace EveUtils.Server.DataExplorer;

/// <summary>One composition with its roles and entries, and the fleets coupled to it.</summary>
public sealed class CompositionListItem
{
    public required FleetComposition Composition { get; init; }
    public required IReadOnlyList<CompositionRoleItem> Roles { get; init; }
    public required IReadOnlyList<FleetListItem> UsedBy { get; init; }

    public IEnumerable<CompositionEntryItem> Entries => Roles.SelectMany(r => r.Entries);

    public int BrokenFitLinks => Entries.Count(e => e.IsLinkBroken);
}
