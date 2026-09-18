namespace EveUtils.Server.DataExplorer;

public sealed class RightNowSnapshot
{
    public required IReadOnlyList<RightNowFleet> Fleets { get; init; }

    /// <summary>Names for the members' portraits: paired characters first, ESI's public lookup for the rest.</summary>
    public required IReadOnlyDictionary<long, string> Names { get; init; }
}
