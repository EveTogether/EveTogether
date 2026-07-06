namespace EveUtils.Shared.Modules.AdminAuth.Permissions;

/// <summary>
/// Server-panel permission codes (<c>module.action</c>). These gate the Blazor admin pages via
/// ASP.NET authorization policies — a separate gate from the CQRS <c>IAccessPolicy</c>, sharing only the
/// code language + the code-derived <c>IPermissionRegistry</c>. Every existing and new panel route is covered.
/// </summary>
public static class PanelPermissions
{
    public const string Module = "Panel";

    public const string DashboardView = "panel.dashboard.view";
    public const string MetricsView = "panel.metrics.view";
    public const string LogsView = "panel.logs.view";
    public const string AllowedManage = "panel.allowed.manage";
    public const string DataView = "panel.data.view";
    public const string DataDelete = "panel.data.delete";
    public const string UsersManage = "panel.users.manage";
    public const string RolesManage = "panel.roles.manage";
    public const string SettingsManage = "panel.settings.manage";

    public static readonly IReadOnlyList<string> All =
    [
        DashboardView, MetricsView, LogsView, AllowedManage,
        DataView, DataDelete, UsersManage, RolesManage, SettingsManage,
    ];
}
