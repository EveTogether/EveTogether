using System.Collections.Generic;

namespace EveUtils.Client.LocalApi.Dtos;

/// <summary>
/// Full detail of one fit: identity, resolved ship name + hull class, the fitted items, and — when <c>?stats=true</c>
/// — the computed Dogma stats. <c>Scope</c> is "local" or "server" (with <c>ServerName</c> set for a server fit).
/// </summary>
public sealed record FitDetailDto(
    int Id,
    string Name,
    string Description,
    int ShipTypeId,
    string ShipName,
    string? HullClass,
    string Scope,
    string? ServerAddress,
    string? ServerName,
    IReadOnlyList<FitItemDto> Items,
    FitStatsDto? Stats);
