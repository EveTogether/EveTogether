using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Dtos;

/// <summary>One run, as much as a publish needs to know about it — the run id to queue, the character it belongs to
/// and where it stands towards a server already. What <c>GetActivityRunsForPublishQuery</c> reads instead of
/// <c>GetActivityDetailQuery</c>'s full loot, bounty, enemy and pricing read, batched over many activities at
/// once (ET-295).</summary>
public sealed record ActivityRunForPublishDto(Guid ActivitySummaryId, Guid RunId, long CharacterId, RunSyncState SyncState);
