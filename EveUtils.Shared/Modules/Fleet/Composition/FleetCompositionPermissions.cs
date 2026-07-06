using EveUtils.Shared.Cqrs.Permissions;

namespace EveUtils.Shared.Modules.Fleet.Composition;

/// <summary>
/// App-permission codes for fleet compositions. A character may manage a composition if they own it
/// (<see cref="FleetComposition.OwnerCharacterId"/>) OR hold <see cref="Manage"/>; the owner-or-right decision is
/// enforced server-side in the mutation handlers via <see cref="FleetCompositionAuthorizer"/> — deliberately NOT
/// as a blocking <c>[RequiresPermission]</c> attribute, because that gate is permission-only and cannot express
/// "owner OR right". The code is registered here so it is assignable through the RBAC roles.
/// </summary>
public static class FleetCompositionPermissions
{
    public const string Manage = "fleet-composition.manage";

    public static IPermissionCatalog Catalog { get; } = new FleetCompositionPermissionCatalog();

    private sealed class FleetCompositionPermissionCatalog : IPermissionCatalog
    {
        public IEnumerable<PermissionDescriptor> Descriptors { get; } =
        [
            new PermissionDescriptor(Manage, "Manage Fleet Compositions",
                "Allow creating, editing and deleting fleet compositions owned by others.", "Fleet Composition"),
        ];
    }
}
