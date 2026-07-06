namespace EveUtils.Shared.Modules.Esi;

/// <summary>Snapshot of the ESI error-limit headers for monitoring.</summary>
public sealed record EsiRateLimitState(int ErrorRemaining, DateTimeOffset ResetAt);
