using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Killmails.Dtos;

namespace EveUtils.Shared.Modules.Killmails.Queries;

/// <summary>Everything the killmail detail screen (ET-333) needs for one mail, priced at today's average.</summary>
public sealed record GetKillmailDetailQuery(int CharacterId, int KillmailId) : IQuery<Result<KillmailDetailDto>>;
