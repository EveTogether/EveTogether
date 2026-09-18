namespace EveUtils.Server.DataExplorer.Destructive;

public enum ConsequenceWeight
{
    Normal,

    /// <summary>Hurts someone right now: a live client, a fleet in op, a fleet left pointing at nothing.</summary>
    Warning,
}
