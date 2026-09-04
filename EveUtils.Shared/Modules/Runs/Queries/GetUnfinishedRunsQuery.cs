using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Dtos;

namespace EveUtils.Shared.Modules.Runs.Queries;

/// <summary>The runs that were stopped and then never saved or thrown away, newest first. Reads <c>Run</c> and not
/// <c>ActivitySummary</c>, because the summary is built from saved runs only — which is why these were invisible
/// everywhere in the app (ET-179).</summary>
public sealed record GetUnfinishedRunsQuery : IQuery<Result<IReadOnlyList<UnfinishedRunDto>>>;
