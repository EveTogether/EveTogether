namespace EveUtils.Client.Runs;

/// <param name="Shared">The figure from a server, or null with <paramref name="Reason"/> saying why there is none.</param>
public sealed record SharedEarningsAttempt(ExpectedEarnings? Shared, OwnRunsReason Reason);
