using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Cqrs.Permissions;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Fleet.Commands;

/// <summary>
/// Persists a fleet's Auto Apply / Auto Invite toggles. These are boss-client automation preferences — the
/// server only stores and relays them so they survive a restart and follow a server fleet across the boss's clients;
/// it never acts on them. Owner-only, <c>fleet.edit</c> gated. Both
/// flags are sent every call (the caller passes the full desired state), so toggling one never clears the other.
/// </summary>
[RequiresPermission(FleetPermissions.Edit)]
public sealed record SetFleetEsiAutomationCommand(
    long FleetId, int ActingCharacterId, bool AutoApplyStructure, bool AutoInviteMembers) : ICommand<Result>;
