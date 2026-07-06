using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Cqrs.Permissions;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Entities;

namespace EveUtils.Shared.Modules.Fleet.Commands;

/// <summary>
/// Moves a roster member to a wing/squad with a role (ESI move-endpoint parity). Creator-only via the
/// member's owning fleet; <c>fleet.structure</c> gated. <see cref="WingId"/>/<see cref="SquadId"/> use
/// the ESI sentinel <c>-1</c> for "none" — the handler normalises them per role.
/// </summary>
[RequiresPermission(FleetPermissions.Structure)]
public sealed record MoveMemberCommand(
    long MemberId, FleetRole Role, long WingId, long SquadId, int ActingCharacterId) : ICommand<Result>;
