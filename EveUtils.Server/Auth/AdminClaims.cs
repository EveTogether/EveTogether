using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using EveUtils.Shared.Modules.AdminAuth.Entities;
using EveUtils.Shared.Modules.AdminAuth.Repositories;

namespace EveUtils.Server.Auth;

/// <summary>
/// Builds the cookie claims for an authenticated admin and a <see cref="Stamp"/> security-stamp that
/// the revalidating auth-state provider compares against the live DB — so a deactivated, demoted or deleted
/// user is signed out instead of running on stale rights until the cookie expires.
/// </summary>
public static class AdminClaims
{
    public const string SuperAdmin = "panel:super";
    public const string Permission = "panel:perm";
    public const string MustChangePassword = "panel:mustchange";
    public const string Stamp = "panel:stamp";

    public static async Task<List<Claim>> BuildAsync(IAdminAuthRepository repository, AdminUser user, CancellationToken cancellationToken = default)
    {
        var (isSuper, codes) = await repository.GetEffectivePermissionsAsync(user.Id, cancellationToken);
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Username),
            new(MustChangePassword, user.MustChangePassword ? "true" : "false"),
            new(Stamp, ComputeStamp(user.IsActive, isSuper, codes)),
        };
        if (isSuper)
            claims.Add(new Claim(SuperAdmin, "true"));
        foreach (var code in codes)
            claims.Add(new Claim(Permission, code));
        return claims;
    }

    /// <summary>Re-derives the stamp from the live user for revalidation; a mismatch forces a re-login.</summary>
    public static async Task<string?> ComputeStampAsync(IAdminAuthRepository repository, AdminUser user, CancellationToken cancellationToken = default)
    {
        var (isSuper, codes) = await repository.GetEffectivePermissionsAsync(user.Id, cancellationToken);
        return ComputeStamp(user.IsActive, isSuper, codes);
    }

    public static bool IsSuperAdmin(this ClaimsPrincipal principal) =>
        principal.HasClaim(SuperAdmin, "true");

    public static bool HasPanelPermission(this ClaimsPrincipal principal, string code) =>
        principal.IsSuperAdmin() || principal.HasClaim(Permission, code);

    private static string ComputeStamp(bool isActive, bool isSuper, IReadOnlyList<string> codes)
    {
        var canonical = $"{(isActive ? 1 : 0)}|{(isSuper ? 1 : 0)}|{string.Join(',', codes)}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash);
    }
}
