namespace EveUtils.Client.Fleet;

/// <summary>
/// Best-effort result of a public-ESI lookup for an external character the owner wants to add to a fleet
/// . <see cref="Exists"/> is false when the id does not resolve (404 / public ESI unreachable);
/// <see cref="Corp"/>/<see cref="Alliance"/> are display labels and may be null even when the character exists.
/// </summary>
public sealed record ExternalCharacterInfo(int CharacterId, string Name, string? Corp, string? Alliance, bool Exists)
{
    /// <summary>An unresolved character (unknown id / lookup failed) — the UI treats it as "not found".</summary>
    public static ExternalCharacterInfo Unknown(int characterId) => new(characterId, string.Empty, null, null, false);
}
