using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Dtos;

namespace EveUtils.Shared.Modules.Runs.Queries;

/// <summary>The run that is running right now, so a window that opens (or reopens) attaches to the stored run
/// instead of starting a second one beside it. Same "exactly one" rule as the loot query — it asks
/// <c>RunningRunLookup</c>, so all three cannot drift apart.</summary>
public sealed record GetRunningRunQuery : IQuery<Result<RunningRunDto>>;
