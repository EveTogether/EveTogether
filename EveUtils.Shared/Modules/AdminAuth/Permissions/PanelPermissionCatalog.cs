using EveUtils.Shared.Cqrs.Permissions;

namespace EveUtils.Shared.Modules.AdminAuth.Permissions;

/// <summary>
/// Declares the server-panel permission codes to the code-derived <see cref="IPermissionRegistry"/>.
/// Registered via <c>AddModulePermissions</c> from <c>AddAdminAuthModule</c>. The ASP.NET authorization
/// policies (one per code) are built from these descriptors at startup.
/// </summary>
public sealed class PanelPermissionCatalog : IPermissionCatalog
{
    public IEnumerable<PermissionDescriptor> Descriptors =>
    [
        new(PanelPermissions.DashboardView, "View dashboard", "View the server dashboard.", PanelPermissions.Module),
        new(PanelPermissions.MetricsView, "View ESI metrics", "View the ESI rate-limit metrics page.", PanelPermissions.Module),
        new(PanelPermissions.LogsView, "View logs", "View the server application log.", PanelPermissions.Module),
        new(PanelPermissions.AllowedManage, "Manage allowed-list", "Manage the pairing allowed-list and server mode.", PanelPermissions.Module),
        new(PanelPermissions.DataView, "View data", "Browse server-stored data (fits, fleets, sessions, characters).", PanelPermissions.Module),
        new(PanelPermissions.DataDelete, "Delete data", "Delete server-stored data.", PanelPermissions.Module),
        new(PanelPermissions.UsersManage, "Manage users", "Create, edit, (de)activate and delete admin users.", PanelPermissions.Module),
        new(PanelPermissions.RolesManage, "Manage roles", "Create and edit roles and their permission codes.", PanelPermissions.Module),
        new(PanelPermissions.SettingsManage, "Manage settings", "Change server panel settings.", PanelPermissions.Module),
    ];
}
