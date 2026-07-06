using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Cqrs.Permissions;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Fleet.Commands;

/// <summary>Deletes a wing; its squads cascade with it (FK). Creator-only; <c>fleet.structure</c> gated.</summary>
[RequiresPermission(FleetPermissions.Structure)]
public sealed record DeleteWingCommand(long WingId, int ActingCharacterId) : ICommand<Result>;
