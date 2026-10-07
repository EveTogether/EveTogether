using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Runs.Commands;

/// <summary>"Re-value at current prices" (ET-463): replaces the fixed unit price of every loot, ore and filament line on
/// these runs with the cache's current one, writing over the value they had when they came in. A correction like any
/// other — a saved run is marked corrected and its activity rebuilt — and only for this machine's own runs. The result
/// is how many runs changed value; a run already at the current prices is left alone.</summary>
public sealed record RevalueRunsCommand(IReadOnlyList<Guid> RunIds) : ICommand<Result<int>>;
