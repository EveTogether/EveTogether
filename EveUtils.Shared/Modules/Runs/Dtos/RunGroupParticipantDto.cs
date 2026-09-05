namespace EveUtils.Shared.Modules.Runs.Dtos;

/// <summary>One run in a live activity, as far as who is on it goes. <see cref="RunId"/> travels along because
/// payout eligibility is set per run, not per character (ET-105) — a character with two runs in the same activity
/// gets two rows here, each toggled on its own.</summary>
public sealed record RunGroupParticipantDto(Guid RunId, long CharacterId, bool IsParticipant, bool IsPayoutEligible);
