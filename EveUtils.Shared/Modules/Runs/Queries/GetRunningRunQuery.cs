using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Dtos;

namespace EveUtils.Shared.Modules.Runs.Queries;

/// <summary>The run that is running right now, so a window that opens (or reopens) attaches to the stored run
/// instead of starting a second one beside it. Asks <c>RunningRunLookup</c>, same as the loot query.</summary>
/// <param name="CharacterId">Scope the answer to one pilot's own run (ET-130 deel 2) — six toons on six sites are
/// six independent questions, not one. Null falls back to the old app-wide count, for a caller with no character of
/// its own to ask about.</param>
/// <param name="RunId">Name the run outright rather than asking "which one is running" (ET-254) — a window resuming
/// a specific run off the UNFINISHED band or a startup notice, neither of which is "the one run running right now":
/// the row is <see cref="Enums.RunState.Stopped"/>, which every other caller of this query must never adopt. Named,
/// it is answered regardless of state and regardless of how many OTHER runs are running elsewhere — there is
/// nothing to disambiguate when the caller already knows which row it wants.</param>
public sealed record GetRunningRunQuery(long? CharacterId = null, Guid? RunId = null) : IQuery<Result<RunningRunDto>>;
