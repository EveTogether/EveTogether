using System.Collections.Generic;

namespace EveUtils.Client.LocalApi.Dtos;

/// <summary>
/// The <c>snapshot</c> event sent to a widget the moment it connects to <c>/ws</c>: the current own-character metrics
/// and the active fleet (null when not participating), so the widget renders immediately instead of waiting for the
/// first live tick.
/// </summary>
public sealed record WsSnapshotDto(
    IReadOnlyList<CharacterMetricsDto> Metrics,
    FleetDetailDto? Fleet);
