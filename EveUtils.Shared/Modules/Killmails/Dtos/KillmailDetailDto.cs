using EveUtils.Shared.Modules.Killmails.Entities;
using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Killmails.Dtos;

/// <summary>One (Flag, TypeId, destroyed-or-dropped) line of a killmail's items, split so a stack that is partly
/// destroyed and partly dropped becomes two lines (ET-333) — each with its own quantity and value.</summary>
public sealed record KillmailDetailItemLineDto(
    int Flag, int TypeId, bool IsNested, bool IsDestroyed, long Quantity, decimal? Value);

/// <summary>One attacker line, ids only; the reader names them and marks <see cref="TopDamage"/> (the highest
/// <see cref="DamageDone"/>, ESI order breaking a tie).</summary>
public sealed record KillmailDetailAttackerLineDto(
    int Ordinal, int? CharacterId, int? CorporationId, int? AllianceId, int? FactionId,
    int? ShipTypeId, int? WeaponTypeId, int DamageDone, bool FinalBlow, bool TopDamage);

/// <summary>The run a loss is linked to, as far as the killmail detail screen's LINKED RUN section needs it (ET-333):
/// its own identity for OPEN RUN, plus the move-to-another-run candidates <see cref="Queries.GetRunLossesQuery"/>
/// already computes (ET-331) — this window carries no linking rule of its own.</summary>
public sealed record KillmailLinkedRunDto(
    Guid RunId, Guid ActivitySummaryId, DateTime StartedAtUtc, string? SiteName, ActivityKind ActivityKind,
    KillmailLinkSource LinkSource, IReadOnlyList<KillmailRunChoiceDto> OtherRuns);

/// <summary>Everything the killmail detail screen (ET-333) reads in one call: the mail, its item and attacker lines,
/// today's value, and the run it is linked to (a loss only). Ids only for names — the reader resolves the SDE and
/// <c>KillmailNames</c> the same way <see cref="Queries.GetKillmailsOverviewQuery"/>'s rows already do.</summary>
public sealed record KillmailDetailDto(
    int CharacterId, int KillmailId, string Hash, DateTime KillmailTimeUtc, int SolarSystemId, bool IsLoss,
    int VictimShipTypeId, decimal? ShipValue, int? VictimCharacterId, int? VictimCorporationId, int? VictimAllianceId,
    int DamageTaken, IReadOnlyList<KillmailDetailItemLineDto> Items, IReadOnlyList<KillmailDetailAttackerLineDto> Attackers,
    KillmailLinkedRunDto? LinkedRun);
