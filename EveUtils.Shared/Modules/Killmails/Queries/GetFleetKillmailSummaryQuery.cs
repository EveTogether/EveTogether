using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Killmails.Dtos;

namespace EveUtils.Shared.Modules.Killmails.Queries;

/// <summary>
/// The stored killmails of <paramref name="CharacterIds"/> that fall in a fleet's active period (ET-372): mails at or after
/// <paramref name="ActiveFromUtc"/>, either the pilot's own or shared in <paramref name="FleetId"/>. A mail a fleet mate
/// shared in another fleet never counts. Local data only; nothing is fetched.
/// </summary>
public sealed record GetFleetKillmailSummaryQuery(long FleetId, DateTime ActiveFromUtc, IReadOnlyCollection<int> CharacterIds)
    : IQuery<Result<FleetKillmailSummaryDto>>;
