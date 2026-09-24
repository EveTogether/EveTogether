namespace EveUtils.Shared.Identity;

/// <summary>
/// A module's share of removing a character from this PC (ET-345): each module deletes what it keeps for that
/// character itself, so the removal never reaches into another module's tables.
/// </summary>
public interface ICharacterDataEraser
{
    /// <summary>Whether this is data that only exists for the character, always deleted, or history the player may
    /// choose to keep.</summary>
    CharacterDataKind Kind { get; }

    /// <summary>Deletes what this module keeps for the character. Its registry row and tokens are already gone, so
    /// nothing re-imports it while this runs.</summary>
    Task EraseAsync(int characterId, string characterName, CancellationToken cancellationToken = default);
}
