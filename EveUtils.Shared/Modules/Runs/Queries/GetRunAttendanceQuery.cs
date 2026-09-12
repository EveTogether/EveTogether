using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Dtos;

namespace EveUtils.Shared.Modules.Runs.Queries;

/// <summary>The attendance decision a homefront's runs carry (ET-230) — the newest one on any run sharing
/// <paramref name="GroupCode"/>, or on <paramref name="RunId"/> when it is flown alone. Null while nobody decided.</summary>
public sealed record GetRunAttendanceQuery(string? GroupCode, Guid RunId) : IQuery<Result<RunAttendanceDecision?>>;
