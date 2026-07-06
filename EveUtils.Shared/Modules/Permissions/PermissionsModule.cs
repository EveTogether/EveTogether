using EveUtils.Shared.Modules.Permissions.Entities;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Permissions;

/// <summary>
/// Server-only permission-toggle persistence. Entity-owning, so it lives in Shared but is
/// only loaded by the server context — the <see cref="PermissionToggle"/> table lands in the server DB.
/// </summary>
public static class PermissionsModule
{
    public static void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new PermissionToggleConfiguration());
    }
}
