namespace EveUtils.Client.ViewModels.Setup;

/// <summary>A character added in this run, as a row with its server chip (or NO SERVER).</summary>
public sealed class SetupCharacterRow(CharacterViewModel character, string? serverName, string? chipText = null)
{
    public CharacterViewModel Character { get; } = character;
    public bool IsCoupled { get; } = serverName is not null || chipText is not null;
    public string ChipText { get; } = chipText ?? serverName?.ToUpperInvariant() ?? "NO SERVER";
}
