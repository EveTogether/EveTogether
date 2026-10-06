namespace EveUtils.Shared.Modules.Killmails.Dtos;

/// <summary>The fleet's killmail totals, each mail counted once, plus one entry per member who has any (ET-372).</summary>
public sealed record FleetKillmailSummaryDto(int Losses, int Kills, IReadOnlyList<FleetMemberKillmailDto> Members);

/// <param name="Kills">Distinct mails this member was an attacker on.</param>
/// <param name="LastShipLoss">The latest loss of a ship other than a capsule, or null when there is none.</param>
/// <param name="PodLosses">Distinct capsule losses; they count in LOSSES, so the chip names them as a pod.</param>
/// <param name="LastPodLossKillmailId">The latest capsule loss, the mail a pod-only chip opens.</param>
public sealed record FleetMemberKillmailDto(
    int CharacterId, int Kills, FleetMemberShipLossDto? LastShipLoss, int PodLosses = 0, int? LastPodLossKillmailId = null);

public sealed record FleetMemberShipLossDto(int KillmailId, int ShipTypeId);
