using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Dtos;

/// <param name="StartedAtUtc">The stored anchor — the clock the window shows is this one, not a second one it kept.</param>
public sealed record RunningRunDto(
    Guid Id,
    long CharacterId,
    ActivityKind ActivityKind,
    DateTime StartedAtUtc,
    string? GroupCode,
    string? SiteName);
