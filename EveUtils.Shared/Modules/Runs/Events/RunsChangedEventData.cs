namespace EveUtils.Shared.Modules.Runs.Events;

/// <param name="RunId">The run that changed, or null when a write reached more runs than one it could name — a
/// rebuild of every summary, the startup clean-up of runs left running.</param>
/// <param name="GroupCode">The run's group code where the writer knows it, so a screen showing a whole group can tell
/// that a run it has never seen just joined it.</param>
public sealed record RunsChangedEventData(Guid? RunId, string? GroupCode);
