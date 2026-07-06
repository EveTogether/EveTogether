using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.AdminAuth.Entities;
using EveUtils.Shared.Modules.AdminAuth.Repositories;
using EveUtils.Shared.Modules.AdminAuth.Services;

namespace EveUtils.Server.Auth;

public readonly record struct AdminActionResult(bool Ok, string? Error)
{
    public static AdminActionResult Success => new(true, null);
    public static AdminActionResult Fail(string error) => new(false, error);
}

/// <summary>
/// User- and role-management with the panel's protection rules: you cannot delete or deactivate
/// yourself, and the last active super-admin can never be removed, deactivated or demoted. Centralised here so
/// every mutation path is guarded and the rules are unit-testable.
/// </summary>
public sealed class AdminManagementService(IAdminAuthRepository repository, IAdminPasswordHasher hasher) : IScopedService
{
    // ── Users ────────────────────────────────────────────────────────────────────────────────────────

    public async Task<AdminActionResult> CreateUserAsync(string username, string temporaryPassword, IReadOnlyList<int> roleIds, CancellationToken ct = default)
    {
        username = (username ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(username))
            return AdminActionResult.Fail("Username is required.");
        if (!PasswordPolicy.IsValid(temporaryPassword))
            return AdminActionResult.Fail(PasswordPolicy.Requirement);

        var normalized = username.ToLowerInvariant();
        if (await repository.FindByNormalizedUsernameAsync(normalized, ct) is not null)
            return AdminActionResult.Fail($"A user named '{username}' already exists.");

        var id = await repository.AddUserAsync(new AdminUser
        {
            Username = username,
            UsernameNormalized = normalized,
            PasswordHash = hasher.Hash(temporaryPassword),
            CreatedAt = DateTimeOffset.UtcNow,
            IsActive = true,
            MustChangePassword = true,
        }, ct);
        await repository.SetUserRolesAsync(id, roleIds, ct);
        return AdminActionResult.Success;
    }

    public async Task<AdminActionResult> ResetPasswordAsync(int targetUserId, string temporaryPassword, CancellationToken ct = default)
    {
        if (!PasswordPolicy.IsValid(temporaryPassword))
            return AdminActionResult.Fail(PasswordPolicy.Requirement);
        var user = await repository.GetUserAsync(targetUserId, ct);
        if (user is null)
            return AdminActionResult.Fail("User not found.");
        user.PasswordHash = hasher.Hash(temporaryPassword);
        user.MustChangePassword = true;
        await repository.UpdateUserAsync(user, ct);
        return AdminActionResult.Success;
    }

    public async Task<AdminActionResult> SetActiveAsync(int actingUserId, int targetUserId, bool active, CancellationToken ct = default)
    {
        var user = await repository.GetUserAsync(targetUserId, ct);
        if (user is null)
            return AdminActionResult.Fail("User not found.");
        if (!active)
        {
            if (targetUserId == actingUserId)
                return AdminActionResult.Fail("You cannot deactivate your own account.");
            if (await WouldStripLastSuperAdminAsync(user, ct))
                return AdminActionResult.Fail("Cannot deactivate the last active super-admin.");
        }
        user.IsActive = active;
        await repository.UpdateUserAsync(user, ct);
        return AdminActionResult.Success;
    }

    public async Task<AdminActionResult> DeleteUserAsync(int actingUserId, int targetUserId, CancellationToken ct = default)
    {
        if (targetUserId == actingUserId)
            return AdminActionResult.Fail("You cannot delete your own account.");
        var user = await repository.GetUserAsync(targetUserId, ct);
        if (user is null)
            return AdminActionResult.Fail("User not found.");
        if (await WouldStripLastSuperAdminAsync(user, ct))
            return AdminActionResult.Fail("Cannot delete the last active super-admin.");
        await repository.DeleteUserAsync(targetUserId, ct);
        return AdminActionResult.Success;
    }

    public async Task<AdminActionResult> SetUserRolesAsync(int targetUserId, IReadOnlyList<int> roleIds, CancellationToken ct = default)
    {
        var user = await repository.GetUserAsync(targetUserId, ct);
        if (user is null)
            return AdminActionResult.Fail("User not found.");

        if (user.IsActive && user.UserRoles.Any(ur => ur.Role.IsSuperAdmin))
        {
            var roles = await repository.ListRolesAsync(ct);
            var keepsSuper = roles.Where(r => roleIds.Contains(r.Id)).Any(r => r.IsSuperAdmin);
            if (!keepsSuper && await repository.CountActiveSuperAdminsAsync(ct) <= 1)
                return AdminActionResult.Fail("Cannot remove the super-admin role from the last active super-admin.");
        }

        await repository.SetUserRolesAsync(targetUserId, roleIds, ct);
        return AdminActionResult.Success;
    }

    // ── Roles ────────────────────────────────────────────────────────────────────────────────────────

    public async Task<AdminActionResult> CreateRoleAsync(string name, string description, bool isSuperAdmin, IReadOnlyList<string> codes, CancellationToken ct = default)
    {
        name = (name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
            return AdminActionResult.Fail("Role name is required.");
        var roles = await repository.ListRolesAsync(ct);
        if (roles.Any(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)))
            return AdminActionResult.Fail($"A role named '{name}' already exists.");

        var id = await repository.AddRoleAsync(new Role { Name = name, Description = description ?? string.Empty, IsSuperAdmin = isSuperAdmin }, ct);
        await repository.SetRolePermissionsAsync(id, codes, ct);
        return AdminActionResult.Success;
    }

    public async Task<AdminActionResult> UpdateRoleAsync(int roleId, string name, string description, IReadOnlyList<string> codes, CancellationToken ct = default)
    {
        name = (name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
            return AdminActionResult.Fail("Role name is required.");
        await repository.UpdateRoleAsync(roleId, name, description ?? string.Empty, ct);
        await repository.SetRolePermissionsAsync(roleId, codes, ct);
        return AdminActionResult.Success;
    }

    public async Task<AdminActionResult> DeleteRoleAsync(int roleId, CancellationToken ct = default)
    {
        var role = await repository.GetRoleAsync(roleId, ct);
        if (role is null)
            return AdminActionResult.Fail("Role not found.");
        if (role.IsSuperAdmin)
        {
            var roles = await repository.ListRolesAsync(ct);
            if (roles.Count(r => r.IsSuperAdmin) <= 1)
                return AdminActionResult.Fail("Cannot delete the last super-admin role.");
        }
        await repository.DeleteRoleAsync(roleId, ct);
        return AdminActionResult.Success;
    }

    /// <summary>True if the (currently active super-admin) user is the only active super-admin left.</summary>
    private async Task<bool> WouldStripLastSuperAdminAsync(AdminUser user, CancellationToken ct)
    {
        var isActiveSuper = user.IsActive && user.UserRoles.Any(ur => ur.Role.IsSuperAdmin);
        return isActiveSuper && await repository.CountActiveSuperAdminsAsync(ct) <= 1;
    }
}
