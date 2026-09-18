namespace EveUtils.Server.DataExplorer;

/// <summary>How far one composition role is filled by the fleet's assigned members.</summary>
public sealed class RoleCoverage
{
    public required string RoleName { get; init; }
    public required int Assigned { get; init; }

    /// <summary><c>GroupMinCount</c>, or else the sum of the entries' <c>EntryMinCount</c>; null when the role sets no
    /// minimum at all, which is shown as a count rather than as a target nobody set.</summary>
    public int? Target { get; init; }

    public required IReadOnlyList<EntryCoverage> Entries { get; init; }

    public bool IsMet => Target is not { } target || Assigned >= target;
}
