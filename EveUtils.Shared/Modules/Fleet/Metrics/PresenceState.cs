namespace EveUtils.Shared.Modules.Fleet.Metrics;

/// <summary>
/// What a member's own client reports about their EVE client, carried as the value of a
/// <see cref="MetricKind.Presence"/> sample. Three states and not a bool, for the same reason
/// <c>ILocalCharacterPresence.IsInGame</c> is nullable: at boot — before the character registry has loaded — a
/// client has no verdict yet, and publishing <see cref="NotInGame"/> then would tell the whole fleet that a pilot who
/// is plainly flying has logged off.
/// </summary>
public enum PresenceState
{
    /// <summary>No verdict. The sample still says "my client is running"; it claims nothing about the game.</summary>
    Unknown = 0,

    /// <summary>EVE Together is running for this character, and no EVE client is.</summary>
    NotInGame = 1,

    /// <summary>EVE Together is running for this character, and so is an EVE client.</summary>
    InGame = 2,
}
