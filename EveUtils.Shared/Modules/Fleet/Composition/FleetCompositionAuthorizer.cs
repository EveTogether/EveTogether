using EveUtils.Shared.Cqrs.Permissions;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Identity;

namespace EveUtils.Shared.Modules.Fleet.Composition;

/// <summary>
/// The one place the composition mutation rule lives: a character may manage a
/// composition if they own it OR the access policy grants them <see cref="FleetCompositionPermissions.Manage"/>.
/// In v1 the policy is <c>OwnerAllPolicy</c> (everyone passes — single-owner deployment); v2 swaps in the
/// group-based policy without touching the handlers. Client-only compositions are owner-only by construction
/// (their owner is the single local principal, and no RBAC exists locally).
/// </summary>
public sealed class FleetCompositionAuthorizer(IAccessPolicy accessPolicy, IPrincipalAccessor principals) : IScopedService
{
    public async Task<bool> CanManageAsync(FleetComposition composition, int actingCharacterId, CancellationToken cancellationToken = default)
        => composition.OwnerCharacterId == actingCharacterId
           || await accessPolicy.IsAllowedAsync(principals.Current, FleetCompositionPermissions.Manage, cancellationToken);
}
