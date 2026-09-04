using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Cqrs.Permissions;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Enums;

namespace EveUtils.Shared.Modules.Fleet.Commands;

/// <summary>
/// Stops a fleet (ET-166): flips its <see cref="Entities.FleetActivation"/> from
/// <see cref="Entities.FleetActivation.Active"/> back to <see cref="Entities.FleetActivation.Forming"/> — the way
/// back that <see cref="ConcludeFleetCommand"/> deliberately is not. The fleet keeps its roster, its name and its
/// coupled doctrine, so a weekly op is started again next week instead of being recreated. The members stay on the
/// roster; only their coupling lapses, because "coupled" is derived from the fleet being Active
/// (<c>IFleetRepository.ListActiveMembershipsAsync</c>) — so they are free to be coupled to another fleet the moment
/// this one stops. Only the creator may stop it (enforced on <see cref="ActingCharacterId"/> in the handler);
/// <c>fleet.edit</c> is gated server-side. Idempotent on an already-Forming fleet; refused on a Concluded one, which
/// is terminal.
///
/// <para><paramref name="Trigger"/> says who decided (ET-167). The automatic stop is not a second command and not a
/// way past the creator check: the sweep sends this one, with the owner's id in <paramref name="ActingCharacterId"/>,
/// so it satisfies the same guard on the same terms a pressed button does. What it may not do is arrive looking like
/// a pressed button — hence the trigger, which decides who is told and in what words.</para>
/// </summary>
/// <param name="Trigger">Who stopped it. Defaults to <see cref="FleetStopTrigger.Manual"/> so every existing caller
/// keeps meaning exactly what it meant.</param>
[RequiresPermission(FleetPermissions.Edit)]
public sealed record StopFleetCommand(
    long FleetId,
    int ActingCharacterId,
    FleetStopTrigger Trigger = FleetStopTrigger.Manual) : ICommand<Result>;
