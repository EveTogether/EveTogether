using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Cqrs.Permissions;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Fleet.Commands;

/// <summary>Deletes a squad. Creator-only via the wing's owning fleet; <c>fleet.structure</c> gated.</summary>
[RequiresPermission(FleetPermissions.Structure)]
public sealed record DeleteSquadCommand(long SquadId, int ActingCharacterId) : ICommand<Result>;
