namespace EveUtils.Server.DataExplorer.Destructive;

/// <summary>One line of what an action will do, worded from the counts the database holds at the moment it is shown.</summary>
public sealed class Consequence
{
    public required string Text { get; init; }
    public ConsequenceWeight Weight { get; init; } = ConsequenceWeight.Normal;
}
