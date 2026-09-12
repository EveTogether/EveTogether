using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Dtos;

/// <summary>
/// One run <see cref="Commands.StopRunsLeftRunningCommand"/> just ended — enough for the startup notice (ET-254) to
/// name it and offer RESUME, without the detail a loot- or bounty-aware DTO like <see cref="UnfinishedRunDto"/>
/// would carry: the notice fires before the main window exists, and the figures it would need are the same ones
/// the UNFINISHED band already shows once that window is up.
/// </summary>
public sealed record StoppedRunDto(
    Guid RunId,
    long CharacterId,
    ActivityKind ActivityKind,
    string? SiteName,
    string? SignatureGroupSnapshot,
    DateTime StoppedAtUtc);
