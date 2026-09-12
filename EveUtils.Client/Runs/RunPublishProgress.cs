namespace EveUtils.Client.Runs;

/// <summary>One group's automatic publish in flight, or the reason the last one failed. Held in memory only: a failed
/// run stays queued in the store, and the next start or reconnect tries it again whatever this said.</summary>
public sealed record RunPublishProgress(string ServerAddress, RunPublishPhase Phase, string? Message = null);
