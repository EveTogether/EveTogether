using EveUtils.Shared.Modules.Fleet.Composition;

namespace EveUtils.Server.DataExplorer;

public sealed class CompositionRoleItem
{
    public required FleetCompositionRole Role { get; init; }
    public required IReadOnlyList<CompositionEntryItem> Entries { get; init; }

    /// <summary>How many pilots the role asks for: its group minimum, else the entries' own minimums added up, else
    /// nothing — the same reading as the fleet pane's coverage, which never invents a minimum of one.</summary>
    public int? Target => Role.GroupMinCount
        ?? (Entries.Any(e => e.Entry.EntryMinCount is not null) ? Entries.Sum(e => e.Entry.EntryMinCount ?? 0) : null);
}
