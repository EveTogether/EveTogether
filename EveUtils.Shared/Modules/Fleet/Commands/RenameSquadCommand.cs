using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Cqrs.Permissions;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Fleet.Commands;

/// <summary>Renames a squad. Creator-only via the wing's owning fleet; <c>fleet.structure</c> gated.</summary>
[RequiresPermission(FleetPermissions.Structure)]
public sealed record RenameSquadCommand(long SquadId, string Name, int ActingCharacterId) : ICommand<Result>;
