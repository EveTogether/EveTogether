using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Cqrs.Permissions;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Fleet.Commands;

/// <summary>
/// The fleet commander's one button (ET-168, scherm 2): ask <i>every</i> member who is active elsewhere to come
/// over, in a single act, whether that is one member or fifty. It asks and nothing more — no roster is touched, no
/// pilot is moved. Pulling someone out of another fleet is the member's own act
/// (<see cref="SwitchToFleetCommand"/>), and the guard in <see cref="ActiveFleetMembershipGuard"/> already says so
/// in as many words: <i>leave or conclude it before joining another</i>.
///
/// Creator-only on a fleet that is running, on the same terms as <see cref="StartFleetCommand"/>. Returns how many
/// members were asked, so the commander gets the count back rather than a bare "done".
///
/// <para>There is deliberately no per-member variant here and no threshold above which the shape changes: starting
/// is one act with one goal, and a member-by-member form is what turns two collisions into paperwork and eleven
/// into a reason not to start. The per-member choice lives outside starting — on the member row in the overview and
/// in fleet management — where it is not in the way.</para>
/// </summary>
/// <param name="OnlyCharacterId">Null asks everyone who is active elsewhere — the start dialog's one button. A
/// character id asks that one member and nobody else, which is what "ask them to switch" on a single member row
/// does. That is the same act at a different scale, not a second kind of request, so it is a parameter here rather
/// than a command of its own.</param>
[RequiresPermission(FleetPermissions.Edit)]
public sealed record RequestFleetSwitchCommand(long FleetId, int ActingCharacterId, int? OnlyCharacterId = null)
    : ICommand<Result<int>>;
