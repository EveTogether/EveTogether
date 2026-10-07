using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Killmails.Dtos;

namespace EveUtils.Shared.Modules.Killmails.Queries;

/// <summary>Every killmail ET keeps for the given characters, newest first (ET-332, ET-405). The caller names them:
/// fleet mates' shared mails live in the same table since ET-371, so "all characters" means the ids of the own
/// registered characters, never everything in the database. A mail two own characters share comes back once per character —
/// merging those is the caller's job, since which one counts as "the" row is a display decision. ESI's own
/// <c>/recent</c> window is 90 days, but a mail already imported stays past that — the importer only ever adds.</summary>
public sealed record GetKillmailsOverviewQuery(IReadOnlyCollection<int> CharacterIds) : IQuery<Result<IReadOnlyList<KillmailOverviewRowDto>>>;
