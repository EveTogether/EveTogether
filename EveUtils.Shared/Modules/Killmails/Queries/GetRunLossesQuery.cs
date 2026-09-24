using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Killmails.Dtos;

namespace EveUtils.Shared.Modules.Killmails.Queries;

/// <summary>The own losses linked to any of <paramref name="RunIds"/>, oldest first (ET-331).</summary>
public sealed record GetRunLossesQuery(IReadOnlyList<Guid> RunIds) : IQuery<Result<IReadOnlyList<RunLossDto>>>;
