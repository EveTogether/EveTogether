using EveUtils.Shared.Cqrs.Permissions;

namespace EveUtils.Shared.Modules.Fleet;

/// <summary>
/// App-permission codes for fleet management. These are server-wide toggles the operator can flip
/// in the admin panel; they default to enabled. Distinct from the per-fleet creator/role checks, which the
/// handlers enforce on the acting character.
/// </summary>
public static class FleetPermissions
{
    public const string Create = "fleet.create";
    public const string Edit = "fleet.edit";
    public const string Disband = "fleet.disband";

    /// <summary>Manage a fleet's wing/squad structure. One toggle covers wing + squad CRUD.</summary>
    public const string Structure = "fleet.structure";

    /// <summary>Invite characters to a fleet.</summary>
    public const string Invite = "fleet.invite";

    /// <summary>Share live activity metrics with a fleet. Gates remote delivery of fleet.metric.</summary>
    public const string Metrics = "fleet.metrics";

    public static IPermissionCatalog Catalog { get; } = new FleetPermissionCatalog();

    private sealed class FleetPermissionCatalog : IPermissionCatalog
    {
        public IEnumerable<PermissionDescriptor> Descriptors { get; } =
        [
            new PermissionDescriptor(Create, "Create Fleet", "Allow creating fleets on this server.", "Fleet"),
            new PermissionDescriptor(Edit, "Edit Fleet", "Allow editing a fleet's details.", "Fleet"),
            new PermissionDescriptor(Disband, "Disband Fleet", "Allow disbanding (archiving) a fleet.", "Fleet"),
            new PermissionDescriptor(Structure, "Manage Fleet Structure", "Allow creating, renaming and deleting a fleet's wings and squads.", "Fleet"),
            new PermissionDescriptor(Invite, "Invite to Fleet", "Allow inviting characters to a fleet.", "Fleet"),
            new PermissionDescriptor(Metrics, "Share Fleet Metrics", "Allow sharing live activity metrics (DPS, mining, …) with a fleet.", "Fleet"),
        ];
    }
}
