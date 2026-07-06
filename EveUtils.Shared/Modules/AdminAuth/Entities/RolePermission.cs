namespace EveUtils.Shared.Modules.AdminAuth.Entities;

/// <summary>One granted panel permission-code on a <see cref="Role"/>. The code is a soft reference
/// to the code-derived <c>IPermissionRegistry</c>; an orphaned code is flagged, never auto-removed.</summary>
public sealed class RolePermission
{
    public int RoleId { get; set; }

    public string PermissionCode { get; set; } = string.Empty;

    public Role Role { get; set; } = default!;
}
