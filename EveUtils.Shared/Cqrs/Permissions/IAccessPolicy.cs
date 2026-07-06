using EveUtils.Shared.Identity;

namespace EveUtils.Shared.Cqrs.Permissions;

/// <summary>
/// Decides whether a principal may perform a capability. v1 = <see cref="OwnerAllPolicy"/>
/// (the local owner has everything); v2 swaps in a group-based policy without touching call sites.
/// </summary>
public interface IAccessPolicy
{
    Task<bool> IsAllowedAsync(Principal principal, string code, CancellationToken cancellationToken = default);
}
