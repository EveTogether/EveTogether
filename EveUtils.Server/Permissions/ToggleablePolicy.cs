using EveUtils.Shared.Cqrs.Permissions;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Fittings;
using EveUtils.Shared.Modules.Fleet;
using EveUtils.Shared.Modules.Permissions.Repositories;

namespace EveUtils.Server.Permissions;

/// <summary>
/// Server access policy backed by <see cref="IPermissionToggleStore"/>. Gated codes
/// (<c>fit.sync</c>, <c>fit.manage</c>, <c>fleet.*</c>) follow their persisted toggle; everything else is
/// allowed (OwnerAllPolicy equivalent). Registered AFTER <c>AddPermissionRegistry</c> so it wins (last wins).
/// </summary>
public sealed class ToggleablePolicy(IPermissionToggleStore toggles) : IAccessPolicy
{
    // Fleet codes are admin-toggleable, default-enabled (no toggle row = enabled).
    private static readonly HashSet<string> Gated =
        [FittingsPermissions.Sync, FittingsPermissions.Manage,
         FleetPermissions.Create, FleetPermissions.Edit, FleetPermissions.Disband, FleetPermissions.Structure,
         FleetPermissions.Invite];

    public Task<bool> IsAllowedAsync(Principal principal, string code, CancellationToken cancellationToken = default)
        => Task.FromResult(!Gated.Contains(code) || toggles.IsEnabled(code));
}
