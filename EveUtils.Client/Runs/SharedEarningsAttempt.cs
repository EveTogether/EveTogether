namespace EveUtils.Client.Runs;

/// <param name="Shared">The server's figure that feeds the panel, null when none could.</param>
/// <param name="Answers">Every server that gave a figure, the one in <paramref name="Shared"/> among them (ET-517): what
/// the source chip offers to switch between.</param>
public sealed record SharedEarningsAttempt(ExpectedEarnings? Shared, OwnRunsReason Reason,
    IReadOnlyList<ExpectedEarnings>? Answers = null)
{
    public IReadOnlyList<ExpectedEarnings> Answers { get; } = Answers ?? (Shared is null ? [] : [Shared]);
}
