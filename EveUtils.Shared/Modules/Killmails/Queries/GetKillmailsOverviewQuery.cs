using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Killmails.Dtos;

namespace EveUtils.Shared.Modules.Killmails.Queries;

/// <summary>Every killmail ET keeps for one character, newest first (ET-332). ESI's own <c>/recent</c> window is 90
/// days, but a mail already imported stays past that — the same read <see cref="EsiKillmailImporter"/> walks.</summary>
public sealed record GetKillmailsOverviewQuery(int CharacterId) : IQuery<Result<IReadOnlyList<KillmailOverviewRowDto>>>;
