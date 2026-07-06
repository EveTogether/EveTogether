using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Fleet.Composition.Commands;

/// <summary>Removes a role-group with its entries. Gated on owner-or-manage.</summary>
public sealed record RemoveFleetCompositionRoleCommand(
    long RoleId,
    int ActingCharacterId) : ICommand<Result>;
