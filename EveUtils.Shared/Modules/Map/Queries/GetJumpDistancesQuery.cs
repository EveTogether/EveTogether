using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Map.Dtos;

namespace EveUtils.Shared.Modules.Map.Queries;

/// <summary>
/// The jump count from one system to every other over the local map (ET-399), offline and without asking ESI — one
/// breadth-first pass that answers a hover over any system afterwards. Fails with <c>UNKNOWN_SYSTEM</c> for a system
/// the map does not hold.
/// </summary>
public sealed record GetJumpDistancesQuery(int FromSystemId) : IQuery<Result<JumpDistancesDto>>;
