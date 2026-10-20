namespace EveUtils.Client.Runs;

/// <summary>
/// Which server feeds the figure when the pilot is coupled to several that can answer (ET-512 decision 10): the server
/// this run belongs to, else the one with the most data for the activity. The advice also has the source chip open a
/// list to switch by hand; that screen is not built, so the choice is made here and nowhere else, ready for a pick to
/// override it.
/// </summary>
public static class SharedServerChoice
{
    /// <param name="answers">One figure per server that could give one, never empty.</param>
    /// <param name="runServer">The server address of the run the figure is for, null when it has none yet.</param>
    public static ExpectedEarnings Pick(IReadOnlyList<ExpectedEarnings> answers, string? runServer) =>
        answers.FirstOrDefault(answer => runServer is not null && answer.ServerAddress == runServer)
        ?? answers
            .OrderByDescending(answer => answer.Figures.EffectiveRunCount)
            .ThenByDescending(answer => answer.PilotCount)
            .ThenBy(answer => answer.ServerAddress, StringComparer.Ordinal)
            .First();
}
