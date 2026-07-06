using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Fleet.Composition.Commands;

/// <summary>Reorders a role-group's fit-entries to the given id order. Gated on owner-or-manage.</summary>
public sealed record ReorderFleetCompositionEntriesCommand(
    long RoleId,
    IReadOnlyList<long> OrderedEntryIds,
    int ActingCharacterId) : ICommand<Result>;
