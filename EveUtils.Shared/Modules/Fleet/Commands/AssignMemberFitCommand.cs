using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Cqrs.Permissions;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Composition;

namespace EveUtils.Shared.Modules.Fleet.Commands;

/// <summary>
/// Assigns (or clears) the fit a roster member flies: the fleet commander sets a member's
/// <see cref="Entities.FleetMember.AssignedFit"/> snapshot, optionally tagged with the coupled-composition entry it
/// fills. Creator-only via the member's owning fleet; <c>fleet.structure</c> gated like the other roster mutations.
/// A null <see cref="Fit"/> clears the assignment.
/// </summary>
[RequiresPermission(FleetPermissions.Structure)]
public sealed record AssignMemberFitCommand(
    long MemberId, FitReference? Fit, long? CompositionEntryId, int ActingCharacterId) : ICommand<Result>;
