using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Dtos;

namespace EveUtils.Shared.Modules.Runs.Queries;

/// <summary>The loot snapshots captured so far on the run that is running right now, for the phase-3 ViewModel that
/// ET-98 phase 4 binds. Fails the same way <c>AddRunLootCaptureCommand</c> does when there isn't exactly one running
/// run — that state is shown, never left as an empty, unexplained list (ET-65 AC-7).</summary>
public sealed record GetRunningRunLootQuery : IQuery<Result<RunLootOverview>>;
