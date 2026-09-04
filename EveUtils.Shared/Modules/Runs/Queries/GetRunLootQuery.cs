using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Dtos;

namespace EveUtils.Shared.Modules.Runs.Queries;

/// <summary>The loot captured on a named run — the one a caller already knows, as opposed to
/// <see cref="GetRunningRunLootQuery"/>'s guess at whichever run happens to be running. A detail screen asks this
/// one: it must show the loot of the run it opened, not of whatever else is on the clock (ET-160).</summary>
public sealed record GetRunLootQuery(Guid RunId) : IQuery<Result<RunLootOverview>>;
