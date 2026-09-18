namespace EveUtils.Server.DataExplorer;

/// <summary>A fleet that is forming or in op, with who is in it and what they fly.</summary>
public sealed class RightNowFleet
{
    public required long Id { get; init; }
    public required string Name { get; init; }
    public required FleetPanelStatus Status { get; init; }
    public required DateTimeOffset LastActivityAt { get; init; }
    public required IReadOnlyList<int> MemberCharacterIds { get; init; }

    /// <summary>Ship type id with how many members fly it; members who have not reported a ship are not counted.</summary>
    public required IReadOnlyList<KeyValuePair<int, int>> Ships { get; init; }

    public string? CompositionName { get; init; }
    public int MembersInShips => Ships.Sum(s => s.Value);
}
