using EveUtils.Shared.Modules.AdminAuth.Entities;

namespace EveUtils.Shared.Modules.AdminAuth.Repositories;

/// <summary>Server-only data access for the admin-auth module (users + roles).</summary>
public interface IAdminAuthRepository
{
    // Users
    Task<AdminUser?> FindByNormalizedUsernameAsync(string usernameNormalized, CancellationToken cancellationToken = default);
    Task<AdminUser?> GetUserAsync(int id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AdminUser>> ListUsersAsync(CancellationToken cancellationToken = default);
    Task<int> AddUserAsync(AdminUser user, CancellationToken cancellationToken = default);
    Task UpdateUserAsync(AdminUser user, CancellationToken cancellationToken = default);
    Task DeleteUserAsync(int id, CancellationToken cancellationToken = default);
    Task SetLastLoginAsync(int id, DateTimeOffset at, CancellationToken cancellationToken = default);
    Task SetUserRolesAsync(int userId, IReadOnlyList<int> roleIds, CancellationToken cancellationToken = default);

    /// <summary>Active users that hold at least one super-admin role.</summary>
    Task<int> CountActiveSuperAdminsAsync(CancellationToken cancellationToken = default);

    /// <summary>The effective panel codes of a user (union over roles) + whether any role is super-admin.</summary>
    Task<(bool IsSuperAdmin, IReadOnlyList<string> Codes)> GetEffectivePermissionsAsync(int userId, CancellationToken cancellationToken = default);

    // Roles
    Task<IReadOnlyList<Role>> ListRolesAsync(CancellationToken cancellationToken = default);
    Task<Role?> GetRoleAsync(int id, CancellationToken cancellationToken = default);
    Task<int> AddRoleAsync(Role role, CancellationToken cancellationToken = default);
    Task UpdateRoleAsync(int id, string name, string description, CancellationToken cancellationToken = default);
    Task SetRolePermissionsAsync(int roleId, IReadOnlyList<string> codes, CancellationToken cancellationToken = default);
    Task DeleteRoleAsync(int id, CancellationToken cancellationToken = default);
}
