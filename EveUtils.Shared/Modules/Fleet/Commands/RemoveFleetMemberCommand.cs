using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Fleet.Commands;

/// <summary>
/// Removes a member from a fleet's roster. The fleet owner may remove any member; a member may remove
/// themselves (leave). The creator can never be removed — by anyone — until ownership is transferred away,
/// so a fleet always has an owner. No server toggle: this is a roster self-/owner-action, not a gated op.
/// </summary>
public sealed record RemoveFleetMemberCommand(long MemberId, int ActingCharacterId) : ICommand<Result>;
