using EveUtils.Shared.Modules.Killmails.Entities;

namespace EveUtils.Shared.Modules.Killmails.Dtos;

/// <summary>
/// One killmail as the KILLMAILS overview lists it (ET-332): a kill or loss of one character, valued at read time
/// from the ship and its items, the same as the loot (ET-329) — the ISK is never stored. <see cref="NotLinkedCandidateCount"/>
/// is only meaningful for an unlinked loss (<see cref="IsLoss"/> and <see cref="RunId"/> null); it is 0 otherwise.
/// </summary>
public sealed record KillmailOverviewRowDto(
    int CharacterId,
    int KillmailId,
    DateTime KillmailTimeUtc,
    int SolarSystemId,
    bool IsLoss,
    int VictimShipTypeId,
    int? VictimCharacterId,
    int? VictimCorporationId,
    int? VictimAllianceId,
    int AttackerCount,
    KillmailFinalBlowDto? FinalBlow,
    Guid? RunId,
    KillmailLinkSource LinkSource,
    int NotLinkedCandidateCount,
    decimal? IskValue);
