namespace EveUtils.Server.DataExplorer;

/// <summary>What on this server hangs off one character, keyed by ESI character id.</summary>
public sealed class CharacterUsage
{
    public static readonly CharacterUsage None = new() { Fleets = 0, SharedFits = 0 };

    /// <summary>Fleets the character is rostered in (owning one without flying in it does not count).</summary>
    public required int Fleets { get; init; }

    public required int SharedFits { get; init; }
}
