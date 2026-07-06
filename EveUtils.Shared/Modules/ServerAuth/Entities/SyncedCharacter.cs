namespace EveUtils.Shared.Modules.ServerAuth.Entities;

/// <summary>
/// A character whose EVE tokens live on the server (Mode B). The refresh token is stored
/// encrypted at rest (AES-256-GCM); the access token is fetched on demand and never persisted.
/// <see cref="GrantedScopes"/> contains the ESI scopes actually granted for this character on the
/// server; stored as a JSON column.
/// </summary>
public sealed class SyncedCharacter
{
    public int Id { get; set; }
    public int EsiCharacterId { get; set; }
    public string CharacterName { get; set; } = string.Empty;
    public byte[] RefreshTokenCipher { get; set; } = [];
    public byte[] RefreshTokenNonce { get; set; } = [];
    public byte[] RefreshTokenTag { get; set; } = [];
    public DateTimeOffset PairedAt { get; set; }
    public DateTimeOffset? LastRefreshedAt { get; set; }
    public string GrantedScopesJson { get; set; } = "[]";

    /// <summary>Convenience accessor over <see cref="GrantedScopesJson"/>. Not mapped to DB.</summary>
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<string> GrantedScopes
    {
        get
        {
            var json = GrantedScopesJson;
            if (string.IsNullOrWhiteSpace(json)) return [];
            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? [];
            }
            catch (System.Text.Json.JsonException)
            {
                // A corrupt scopes column must not throw in the middle of an authorization check; treat it as no
                // granted scopes (deny) rather than crashing the request.
                return [];
            }
        }
        set => GrantedScopesJson = System.Text.Json.JsonSerializer.Serialize(value);
    }
}
