using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Fleet.Composition.Commands;

/// <summary>Adds a role-group to a composition: a labelled bucket with an optional group-minimum. Gated
/// on owner-or-manage; appended at the end. Returns the new role's id.</summary>
public sealed record AddFleetCompositionRoleCommand(
    long CompositionId,
    string RoleName,
    int? GroupMinCount,
    int ActingCharacterId) : ICommand<Result<long>>;
