using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Map.Dtos;

namespace EveUtils.Shared.Modules.Map.Queries;

/// <summary>The k-space map of the current SDE build (ET-392); fails with <c>SDE_OUTDATED</c> while the store has none.</summary>
public sealed record GetMapGraphQuery : IQuery<Result<MapGraphDto>>;
