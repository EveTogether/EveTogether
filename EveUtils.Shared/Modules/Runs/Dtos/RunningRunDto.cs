using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Dtos;

/// <param name="StartedAtUtc">The stored anchor — the clock the window shows is this one, not a second one it kept.</param>
public sealed record RunningRunDto(
    Guid Id,
    long CharacterId,
    ActivityKind ActivityKind,
    DateTime StartedAtUtc,
    string? GroupCode,
    string? SiteName,
    /// <summary>The scan id this run was started from, e.g. RUS-326. Two runs of the same site are still two runs,
    /// and this is the only thing that tells them apart.</summary>
    string? Signature);
