namespace EveUtils.Shared.Modules.Runs.Dtos;

/// <summary>A run in <paramref name="GroupCode"/> was pushed to the server (ET-245). Names the group and nothing else:
/// the receiver pulls it over the ordinary authenticated pull, which is what decides what it may read.</summary>
public sealed record RunGroupUpdate(string GroupCode);
