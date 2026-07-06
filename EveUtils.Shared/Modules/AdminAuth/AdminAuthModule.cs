using EveUtils.Shared.Cqrs.Permissions;
using EveUtils.Shared.Modules.AdminAuth.Entities;
using EveUtils.Shared.Modules.AdminAuth.Permissions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Shared.Modules.AdminAuth;

/// <summary>
/// Server-only admin-auth module: admin users, roles, role-permissions and user-roles for the
/// Blazor server panel. Entity-owning, so it lives in Shared but is only loaded by the server context —
/// the tables land in the server DB. The repository + password hasher auto-register via their markers;
/// this only declares the panel permission catalog to the code-derived registry.
/// </summary>
public static class AdminAuthModule
{
    public static void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new AdminUserConfiguration());
        modelBuilder.ApplyConfiguration(new RoleConfiguration());
        modelBuilder.ApplyConfiguration(new RolePermissionConfiguration());
        modelBuilder.ApplyConfiguration(new AdminUserRoleConfiguration());
    }

    public static IServiceCollection AddAdminAuthModule(this IServiceCollection services)
    {
        services.AddModulePermissions(new PanelPermissionCatalog());
        return services;
    }
}
