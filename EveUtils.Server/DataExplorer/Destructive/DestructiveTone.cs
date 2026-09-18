namespace EveUtils.Server.DataExplorer.Destructive;

/// <summary>How much an action destroys, which decides the colour of its button.</summary>
public enum DestructiveTone
{
    /// <summary>Something a later step or the pilot can recover from: a disband (the sweep finishes it) or a revoke (pair again).</summary>
    Recoverable,
    Irreversible,
}
