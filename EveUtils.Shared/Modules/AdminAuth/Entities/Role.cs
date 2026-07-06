namespace EveUtils.Shared.Modules.AdminAuth.Entities;

/// <summary>
/// An RBAC role. A role grants a set of panel permission-codes; a <see cref="IsSuperAdmin"/> role
/// passes <em>every</em> policy (also future codes), which avoids a coverage gap and a lock-out.
/// </summary>
public sealed class Role
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    /// <summary>A super-admin role passes all authorization policies regardless of its granted codes.</summary>
    public bool IsSuperAdmin { get; set; }

    public List<RolePermission> Permissions { get; set; } = [];

    public List<AdminUserRole> UserRoles { get; set; } = [];
}
