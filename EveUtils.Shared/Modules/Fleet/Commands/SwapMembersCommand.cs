using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Cqrs.Permissions;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Fleet.Commands;

/// <summary>
/// Swaps the exact roster positions (role + wing + squad) of two members in the same fleet (stream G, roster
/// drag-and-drop): dragging a pilot onto an occupied commander slot exchanges the two rather than being rejected by
/// the move-endpoint's command-slot-uniqueness rule. The fit each pilot flies stays with the pilot. Creator-only via
/// the owning fleet; <c>fleet.structure</c> gated.
/// </summary>
[RequiresPermission(FleetPermissions.Structure)]
public sealed record SwapMembersCommand(long FirstMemberId, long SecondMemberId, int ActingCharacterId) : ICommand<Result>;
