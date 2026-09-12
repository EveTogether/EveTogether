namespace EveUtils.Shared.Modules.Runs.Dtos;

/// <summary>One ore's aggregated mining, scoped to whichever participant it is read alongside (ET-229) — no RunId of
/// its own, since the caller already knows whose run this is.</summary>
public sealed record RunMiningOreDto(string OreType, int Units, int CriticalUnits, int ResidueUnits);

/// <summary>One run in a live activity, as far as who is on it goes. <see cref="RunId"/> travels along because
/// payout eligibility is set per run, not per character (ET-105) — a character with two runs in the same activity
/// gets two rows here, each toggled on its own.
///
/// <see cref="BountyIsk"/> is this run's own <c>RunBountyEntry</c> total (ET-219), read regardless of run state —
/// the live window asks this before there is a fleet, or any save, to sum a group's bounty against (ET-257).
/// <see cref="MiningEntries"/> is the same run's own <c>RunMiningEntry</c> rows (ET-229), read the same way.
/// <see cref="InSiteAtCompletion"/> is the homefront attendance tick (ET-230), null while nobody decided.</summary>
public sealed record RunGroupParticipantDto(
    Guid RunId, long CharacterId, bool IsParticipant, bool IsPayoutEligible, decimal BountyIsk,
    IReadOnlyList<RunMiningOreDto> MiningEntries, bool? InSiteAtCompletion = null);
