namespace EveUtils.Shared.Modules.AdminAuth.Entities;

/// <summary>
/// A server-panel administrator (cookie-auth, separate from ESI character-pairing). Entity-owning module,
/// so it lives in Shared but is only loaded by the server context — the table lands in the server DB.
/// </summary>
public sealed class AdminUser
{
    public int Id { get; set; }

    public string Username { get; set; } = string.Empty;

    /// <summary>Lowercased username — the unique index sits here so uniqueness is case-insensitive and
    /// consistent across all four providers (no DB-collation precedent in this project).</summary>
    public string UsernameNormalized { get; set; } = string.Empty;

    public string PasswordHash { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? LastLoginAt { get; set; }

    public bool IsActive { get; set; } = true;

    public bool MustChangePassword { get; set; }

    public List<AdminUserRole> UserRoles { get; set; } = [];
}
