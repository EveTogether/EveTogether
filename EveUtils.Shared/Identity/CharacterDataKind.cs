namespace EveUtils.Shared.Identity;

/// <summary>What removing a character does with a module's data for it (ET-345).</summary>
public enum CharacterDataKind
{
    /// <summary>Only exists for this character (skills, implants, killmails, messages, cached metrics): always deleted.</summary>
    Cache,

    /// <summary>What the character did (runs, fittings): kept unless the player asks to delete it too.</summary>
    History
}
