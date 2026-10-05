using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Runs.Queries;

/// <summary>The escalation sites this store has registered before from runs of one source site, most often first
/// (ET-451) — the only source→escalation mapping there is, since neither the SDE nor ESI carries one.</summary>
/// <param name="SourceDungeonId">The source run's own catalogue site.</param>
public sealed record GetEscalationHistoryQuery(int SourceDungeonId) : IQuery<Result<IReadOnlyList<int>>>;
