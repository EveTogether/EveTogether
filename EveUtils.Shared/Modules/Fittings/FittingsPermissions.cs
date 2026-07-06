using EveUtils.Shared.Cqrs.Permissions;

namespace EveUtils.Shared.Modules.Fittings;

/// <summary>
/// App-permission codes for the Fittings module. Only server-gated actions get a code;
/// local ESI calls (import, push) are gated only by ESI-scope checks, not app permissions.
/// </summary>
public static class FittingsPermissions
{
    /// <summary>Controls sharing/syncing fits via the server (two-layer gate).</summary>
    public const string Sync = "fit.sync";

    /// <summary>Controls managing (e.g. deleting) fits in the server's shared library.</summary>
    public const string Manage = "fit.manage";

    public static IPermissionCatalog Catalog { get; } = new FittingsPermissionCatalog();

    private sealed class FittingsPermissionCatalog : IPermissionCatalog
    {
        public IEnumerable<PermissionDescriptor> Descriptors { get; } =
        [
            new PermissionDescriptor(Sync, "Sync Fitting", "Share/sync fittings via the server.", "Fittings"),
            new PermissionDescriptor(Manage, "Manage Shared Fits", "Delete fits from the server's shared library.", "Fittings"),
        ];
    }
}
