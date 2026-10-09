using EveUtils.Shared.Modules.Killmails.Entities;

namespace EveUtils.Shared.Modules.Killmails.Dtos;

/// <summary>Who dealt a killmail's final blow, ids only; the reader names them.</summary>
public sealed record KillmailFinalBlowDto(int? CharacterId, int? CorporationId, int? FactionId, int? ShipTypeId);

/// <summary>A run the pilot may move a loss to: one of the same character's runs whose time holds the loss.</summary>
public sealed record KillmailRunChoiceDto(Guid RunId, string? SiteName, DateTime StartedAtUtc);

/// <summary>One own loss linked to a run (ET-331), with the runs it may be moved to instead. <paramref name="IskValue"/>
/// is what the loss cost the run — the hull and every item at the price the run fixed for it (ET-464) — or null when
/// nothing on it could be priced.</summary>
public sealed record RunLossDto(
    int CharacterId,
    int KillmailId,
    Guid RunId,
    DateTime KillmailTimeUtc,
    int VictimShipTypeId,
    KillmailLinkSource LinkSource,
    KillmailFinalBlowDto? FinalBlow,
    IReadOnlyList<KillmailRunChoiceDto> OtherRuns,
    decimal? IskValue = null);
