using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Dtos;

namespace EveUtils.Shared.Modules.Runs.Queries;

/// <summary>The saved runs one escalation can be linked to afterwards (ET-489): site runs of the escalation's site,
/// started after the run it came from and within its validity, not yet tied to another escalation. Best match first —
/// the same pilot, then the same system, then the run closest after the source.</summary>
public sealed record GetLinkableRunsQuery(Guid SourceRunId, Guid? EntryId)
    : IQuery<Result<IReadOnlyList<LinkableRunDto>>>;
