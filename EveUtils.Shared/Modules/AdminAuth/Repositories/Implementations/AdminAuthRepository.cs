using EveUtils.Shared.Data;
using EveUtils.Shared.Modules.AdminAuth.Entities;
using EveUtils.Shared.Modules.AdminAuth.Repositories;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.AdminAuth.Repositories.Implementations;

/// <summary>Server-only data access. Loaded by the server context, so these tables live in the server DB.</summary>
internal sealed class AdminAuthRepository(IDbContextFactory<SharedDbContext> contextFactory) : IAdminAuthRepository
{
    public async Task<AdminUser?> FindByNormalizedUsernameAsync(string usernameNormalized, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Set<AdminUser>().AsNoTracking()
            .Include(u => u.UserRoles).ThenInclude(ur => ur.Role).ThenInclude(r => r.Permissions)
            .FirstOrDefaultAsync(u => u.UsernameNormalized == usernameNormalized, cancellationToken);
    }

    public async Task<AdminUser?> GetUserAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Set<AdminUser>().AsNoTracking()
            .Include(u => u.UserRoles).ThenInclude(ur => ur.Role).ThenInclude(r => r.Permissions)
            .FirstOrDefaultAsync(u => u.Id == id, cancellationToken);
    }

    public async Task<IReadOnlyList<AdminUser>> ListUsersAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Set<AdminUser>().AsNoTracking()
            .Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
            .OrderBy(u => u.UsernameNormalized)
            .ToListAsync(cancellationToken);
    }

    public async Task<int> AddUserAsync(AdminUser user, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        db.Set<AdminUser>().Add(user);
        await db.SaveChangesAsync(cancellationToken);
        return user.Id;
    }

    public async Task UpdateUserAsync(AdminUser user, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.Set<AdminUser>().FirstOrDefaultAsync(u => u.Id == user.Id, cancellationToken);
        if (row is null)
            return;
        row.Username = user.Username;
        row.UsernameNormalized = user.UsernameNormalized;
        row.PasswordHash = user.PasswordHash;
        row.IsActive = user.IsActive;
        row.MustChangePassword = user.MustChangePassword;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteUserAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.Set<AdminUser>().FirstOrDefaultAsync(u => u.Id == id, cancellationToken);
        if (row is not null)
        {
            db.Remove(row);
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task SetLastLoginAsync(int id, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.Set<AdminUser>().FirstOrDefaultAsync(u => u.Id == id, cancellationToken);
        if (row is null)
            return;
        row.LastLoginAt = at;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetUserRolesAsync(int userId, IReadOnlyList<int> roleIds, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var user = await db.Set<AdminUser>().Include(u => u.UserRoles)
            .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
            return;
        user.UserRoles.Clear();
        foreach (var roleId in roleIds.Distinct())
            user.UserRoles.Add(new AdminUserRole { AdminUserId = userId, RoleId = roleId });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<int> CountActiveSuperAdminsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Set<AdminUser>().AsNoTracking()
            .CountAsync(u => u.IsActive && u.UserRoles.Any(ur => ur.Role.IsSuperAdmin), cancellationToken);
    }

    public async Task<(bool IsSuperAdmin, IReadOnlyList<string> Codes)> GetEffectivePermissionsAsync(int userId, CancellationToken cancellationToken = default)
    {
        var user = await GetUserAsync(userId, cancellationToken);
        if (user is null)
            return (false, []);
        var isSuper = user.UserRoles.Any(ur => ur.Role.IsSuperAdmin);
        var codes = user.UserRoles
            .SelectMany(ur => ur.Role.Permissions.Select(p => p.PermissionCode))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();
        return (isSuper, codes);
    }

    public async Task<IReadOnlyList<Role>> ListRolesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Set<Role>().AsNoTracking()
            .Include(r => r.Permissions)
            .OrderBy(r => r.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task<Role?> GetRoleAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Set<Role>().AsNoTracking()
            .Include(r => r.Permissions)
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
    }

    public async Task<int> AddRoleAsync(Role role, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        db.Set<Role>().Add(role);
        await db.SaveChangesAsync(cancellationToken);
        return role.Id;
    }

    public async Task UpdateRoleAsync(int id, string name, string description, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.Set<Role>().FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (row is null)
            return;
        row.Name = name;
        row.Description = description;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetRolePermissionsAsync(int roleId, IReadOnlyList<string> codes, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var role = await db.Set<Role>().Include(r => r.Permissions)
            .FirstOrDefaultAsync(r => r.Id == roleId, cancellationToken);
        if (role is null)
            return;
        role.Permissions.Clear();
        foreach (var code in codes.Distinct(StringComparer.Ordinal))
            role.Permissions.Add(new RolePermission { RoleId = roleId, PermissionCode = code });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteRoleAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.Set<Role>().FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (row is not null)
        {
            db.Remove(row);
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
