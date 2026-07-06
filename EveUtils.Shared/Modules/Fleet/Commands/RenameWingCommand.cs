using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Cqrs.Permissions;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Fleet.Commands;

/// <summary>Renames a wing. Creator-only via the owning fleet; <c>fleet.structure</c> gated.</summary>
[RequiresPermission(FleetPermissions.Structure)]
public sealed record RenameWingCommand(long WingId, string Name, int ActingCharacterId) : ICommand<Result>;
