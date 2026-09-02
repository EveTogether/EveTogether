namespace EveUtils.Shared.Modules.ApiKeys.Entities;

/// <summary>
/// A managed key for an external consumer of the read-only server REST API. Only the plaintext
/// <see cref="Prefix"/> and the SHA-256 <see cref="SecretHash"/> are stored — the full key is shown once at
/// creation and is unrecoverable afterwards.
/// </summary>
public sealed class ApiKey
{
    public int Id { get; set; }
    public string Label { get; set; } = string.Empty;

    /// <summary>Plaintext lookup handle, unique: it turns key validation into one indexed read.</summary>
    public string Prefix { get; set; } = string.Empty;

    public string SecretHash { get; set; } = string.Empty;

    /// <summary>Comma-separated scope codes. v1 issues only <c>read:all</c>; the field carries later granularity.</summary>
    public string Scopes { get; set; } = string.Empty;

    /// <summary>Null = admin scope over all server data; set = scoped to that character.</summary>
    public int? OwnerCharacterId { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Admin username that created the key — kept as text so deleting the admin user leaves the trail.</summary>
    public string CreatedBy { get; set; } = string.Empty;

    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
}
