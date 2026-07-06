using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Fleet.Composition.Commands;

/// <summary>Reorders a composition's role-groups to the given id order. Gated on owner-or-manage.</summary>
public sealed record ReorderFleetCompositionRolesCommand(
    long CompositionId,
    IReadOnlyList<long> OrderedRoleIds,
    int ActingCharacterId) : ICommand<Result>;
