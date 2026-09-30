using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Killmails.Dtos;

namespace EveUtils.Shared.Modules.Killmails.Queries;

/// <summary>Every killmail ET keeps for one character, newest first (ET-332), or for every character when
/// <paramref name="CharacterId"/> is null (ET-405). A mail two own characters share comes back once per character —
/// merging those is the caller's job, since which one counts as "the" row is a display decision. ESI's own
/// <c>/recent</c> window is 90 days, but a mail already imported stays past that — the importer only ever adds.</summary>
public sealed record GetKillmailsOverviewQuery(int? CharacterId = null) : IQuery<Result<IReadOnlyList<KillmailOverviewRowDto>>>;
