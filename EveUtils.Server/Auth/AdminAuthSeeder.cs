using EveUtils.Shared.Modules.AdminAuth.Entities;
using EveUtils.Shared.Modules.AdminAuth.Permissions;
using EveUtils.Shared.Modules.AdminAuth.Repositories;
using EveUtils.Shared.Modules.AdminAuth.Services;

namespace EveUtils.Server.Auth;

/// <summary>
/// Idempotent startup seed: the built-in roles (<c>Administrator</c> super-admin + <c>Viewer</c>) and
/// the bootstrap <c>admin</c> user with a forced password change. The seed password defaults to <c>admin</c>
/// and is overridable via <c>Server:AdminSeedPassword</c>.
/// </summary>
public static class AdminAuthSeeder
{
    public const string AdministratorRole = "Administrator";
    public const string ViewerRole = "Viewer";
    public const string SeedUsername = "admin";

    public static async Task SeedAsync(IAdminAuthRepository repository, IAdminPasswordHasher hasher, string seedPassword, CancellationToken cancellationToken = default)
    {
        var roles = await repository.ListRolesAsync(cancellationToken);

        var admin = roles.FirstOrDefault(r => string.Equals(r.Name, AdministratorRole, StringComparison.OrdinalIgnoreCase));
        if (admin is null)
        {
            var id = await repository.AddRoleAsync(new Role
            {
                Name = AdministratorRole,
                Description = "Full access to the server panel.",
                IsSuperAdmin = true,
            }, cancellationToken);
            admin = await repository.GetRoleAsync(id, cancellationToken);
        }

        if (!roles.Any(r => string.Equals(r.Name, ViewerRole, StringComparison.OrdinalIgnoreCase)))
        {
            var viewCodes = PanelPermissions.All.Where(c => c.EndsWith(".view", StringComparison.Ordinal)).ToList();
            var id = await repository.AddRoleAsync(new Role
            {
                Name = ViewerRole,
                Description = "Read-only access to the server panel.",
            }, cancellationToken);
            await repository.SetRolePermissionsAsync(id, viewCodes, cancellationToken);
        }

        var normalized = SeedUsername.ToLowerInvariant();
        var existing = await repository.FindByNormalizedUsernameAsync(normalized, cancellationToken);
        if (existing is null && admin is not null)
        {
            var userId = await repository.AddUserAsync(new AdminUser
            {
                Username = SeedUsername,
                UsernameNormalized = normalized,
                PasswordHash = hasher.Hash(seedPassword),
                CreatedAt = DateTimeOffset.UtcNow,
                IsActive = true,
                MustChangePassword = true,
            }, cancellationToken);
            await repository.SetUserRolesAsync(userId, [admin.Id], cancellationToken);
        }
    }
}
