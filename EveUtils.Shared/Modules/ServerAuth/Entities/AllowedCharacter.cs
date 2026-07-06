namespace EveUtils.Shared.Modules.ServerAuth.Entities;

/// <summary>The server's allowed-list: which characters may pair (checked before completing).</summary>
public sealed class AllowedCharacter
{
    public int Id { get; set; }
    public int? EsiCharacterId { get; set; }
    public string CharacterName { get; set; } = string.Empty;
    public string? Note { get; set; }
}
