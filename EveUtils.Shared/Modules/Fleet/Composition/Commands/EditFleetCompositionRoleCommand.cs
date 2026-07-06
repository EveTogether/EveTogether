using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Fleet.Composition.Commands;

/// <summary>Renames a role-group and sets its group-minimum. Null clears the minimum.
/// Gated on owner-or-manage.</summary>
public sealed record EditFleetCompositionRoleCommand(
    long RoleId,
    string RoleName,
    int? GroupMinCount,
    int ActingCharacterId) : ICommand<Result>;
