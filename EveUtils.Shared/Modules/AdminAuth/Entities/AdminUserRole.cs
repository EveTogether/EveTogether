namespace EveUtils.Shared.Modules.AdminAuth.Entities;

/// <summary>Many-to-many link between an <see cref="AdminUser"/> and a <see cref="Role"/>.</summary>
public sealed class AdminUserRole
{
    public int AdminUserId { get; set; }

    public int RoleId { get; set; }

    public AdminUser AdminUser { get; set; } = default!;

    public Role Role { get; set; } = default!;
}
