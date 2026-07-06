namespace EveUtils.Shared.Modules.Esi.Http;

/// <summary>
/// Retry/backoff strategy for the ESI retry handler. The delay schedule is explicit per attempt rather than a
/// pure exponential curve, so we can give ESI a touch more room without an aggressive 1→2→4 ramp (pinned at
/// 1s/3s/5s). Injected so tests can dial the delays right down; production uses <see cref="Default"/>.
/// </summary>
/// <param name="Delays">Wait before each retry: index 0 = before the first retry, etc. Count = the max retries.</param>
/// <param name="MaxDelay">Upper bound on any single wait (also caps a hostile <c>Retry-After</c>).</param>
public sealed record EsiRetryPolicy(IReadOnlyList<TimeSpan> Delays, TimeSpan MaxDelay)
{
    /// <summary>Max retries after the first try (the first try is not a retry).</summary>
    public int MaxRetries => Delays.Count;

    /// <summary>The base wait before the given retry attempt; the last entry holds for any further attempts.</summary>
    public TimeSpan DelayFor(int attempt) => Delays.Count == 0 ? TimeSpan.Zero : Delays[Math.Min(attempt, Delays.Count - 1)];

    public static readonly EsiRetryPolicy Default = new(
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5)],
        TimeSpan.FromSeconds(30));
}
