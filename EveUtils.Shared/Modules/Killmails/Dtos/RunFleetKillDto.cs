namespace EveUtils.Shared.Modules.Killmails.Dtos;

/// <summary>One kill the fleet of a group run made (ET-373), once per killmail however many members were attackers.
/// <paramref name="OpenCharacterId"/> is a member whose stored row opens the killmail detail.
/// <paramref name="HasPod"/> folds the victim's capsule into its ship's line.</summary>
public sealed record RunFleetKillDto(
    int KillmailId, int OpenCharacterId, DateTime KillmailTimeUtc, int VictimShipTypeId, bool HasPod, int? VictimCharacterId,
    int? VictimCorporationId, IReadOnlyList<int> MemberCharacterIds, KillmailFinalBlowDto? FinalBlow, decimal? DestroyedValue);
