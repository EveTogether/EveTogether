using System;
using System.Collections.Generic;
using EveUtils.Shared.Modules.Fleet.Dtos;

namespace EveUtils.Client.Fleet;

/// <summary>
/// Who is where: the newest known position per character, merged from the own gamelog, the own ESI location watch,
/// fleet mates' shared location samples and the in-game fleet roster. In memory only — nothing is stored or sent.
/// </summary>
public interface IFleetPositionSource
{
    /// <summary>The newest known position of every character seen so far.</summary>
    IReadOnlyList<FleetPositionDto> GetPositions();

    /// <summary>
    /// Whether this pilot is known to be out of game (ET-70: one verdict for every screen). A known-offline pilot's
    /// last position is where they logged off, so the map leaves them out; nothing known about them is not offline.
    /// </summary>
    bool IsOffline(int characterId);

    /// <summary>
    /// Raised when a character appears or moves to another system; a repeat sighting in the same system only refreshes
    /// <see cref="FleetPositionDto.ObservedAt"/> and raises nothing. Raised on whichever thread observed it (gamelog
    /// reader, event bus, ESI poll) — a UI consumer marshals to its own thread and must not block the caller.
    /// </summary>
    event Action<FleetPositionDto>? PositionChanged;
}
