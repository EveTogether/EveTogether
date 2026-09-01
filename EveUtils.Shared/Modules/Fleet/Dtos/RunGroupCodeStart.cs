using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Fleet.Dtos;

public sealed record RunGroupCodeStart(long FleetId, ActivityKind ActivityKind, string GroupCode, DateTime StartedAtUtc);
