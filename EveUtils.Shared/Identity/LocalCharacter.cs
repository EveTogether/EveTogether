namespace EveUtils.Shared.Identity;

/// <summary>
/// EF-persisted local character: the SQLite-backed registry row in the client DB (table
/// "LocalCharacter", mirroring the Local* client-side naming of <c>LocalFitting</c>). Keyed by the ESI
/// character id; granted ESI scopes are stored as a JSON array string. The domain-facing type stays
/// <see cref="Character"/>; <see cref="EfCharacterRegistry"/> maps between the two.
/// </summary>
public sealed class LocalCharacter
{
    public int EsiCharacterId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string GrantedScopesJson { get; set; } = "[]";

    /// <summary>User-defined position of this character in the list (lower = higher up). Drives the order shown
    /// everywhere the character list is read (panel, metrics, pickers). New characters append to the end; a drag
    /// re-orders persists here so the next launch keeps the order.</summary>
    public int SortOrder { get; set; }
}
