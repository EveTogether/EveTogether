using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Dtos;

namespace EveUtils.Shared.Modules.Runs.Queries;

/// <summary>The loot captured on a named run — the one a caller already knows. There used to be a second query
/// beside this one that guessed at whichever run happened to be running; the activity window's own LOOT section
/// asks this one now, because it must show the loot of the run it is on and not of whatever else is on the clock
/// (ET-160, and Raymond 2026-09-04 when the guess became ambiguous forever).</summary>
public sealed record GetRunLootQuery(Guid RunId) : IQuery<Result<RunLootOverview>>;
