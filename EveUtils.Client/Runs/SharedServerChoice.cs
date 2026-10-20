namespace EveUtils.Client.Runs;

/// <summary>
/// Which server feeds the figure when the pilot is coupled to several that can answer (ET-512 decision 10): the one
/// the pilot picked by hand on the source chip and the choice was remembered, else the server this run belongs to, else
/// the one with the most data for the activity. The choice is made here and nowhere else.
/// </summary>
public static class SharedServerChoice
{
    /// <param name="answers">One figure per server that could give one, never empty.</param>
    /// <param name="runServer">The server address of the run the figure is for, null when it has none yet.</param>
    /// <param name="pinnedServer">The server the pilot picked for this activity, null when they never did. A pick for a
    /// server that gave no figure now is ignored, not an error: the pilot may have uncoupled it since.</param>
    public static ExpectedEarnings Pick(IReadOnlyList<ExpectedEarnings> answers, string? runServer, string? pinnedServer = null) =>
        answers.FirstOrDefault(answer => pinnedServer is not null && answer.ServerAddress == pinnedServer)
        ?? answers.FirstOrDefault(answer => runServer is not null && answer.ServerAddress == runServer)
        ?? answers
            .OrderByDescending(answer => answer.Figures.EffectiveRunCount)
            .ThenByDescending(answer => answer.PilotCount)
            .ThenBy(answer => answer.ServerAddress, StringComparer.Ordinal)
            .First();
}
