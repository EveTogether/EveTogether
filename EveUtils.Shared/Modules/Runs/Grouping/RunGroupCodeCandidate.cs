namespace EveUtils.Shared.Modules.Runs.Grouping;

public sealed record RunGroupCodeCandidate(string GroupCode, DateTime StartedAtUtc, bool IsFleetCommander = false);
