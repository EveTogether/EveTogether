using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Dtos;

namespace EveUtils.Shared.Modules.Runs.Queries;

/// <summary>The run that is running right now, so a window that opens (or reopens) attaches to the stored run
/// instead of starting a second one beside it. Asks <c>RunningRunLookup</c>, same as the loot query.</summary>
/// <param name="CharacterId">Scope the answer to one pilot's own run (ET-130 deel 2) — six toons on six sites are
/// six independent questions, not one. Null falls back to the old app-wide count, for a caller with no character of
/// its own to ask about.</param>
public sealed record GetRunningRunQuery(long? CharacterId = null) : IQuery<Result<RunningRunDto>>;
