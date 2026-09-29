using System;
using EveUtils.Shared.Modules.Fleet.Enums;

namespace EveUtils.Shared.Modules.Fleet.Dtos;

/// <summary>
/// The newest known position of one character — an own character, an ET fleet mate or an in-game fleet member who
/// does not use EVE Together. Held in memory only, never stored.
/// </summary>
/// <param name="CharacterId">The EVE character id.</param>
/// <param name="Name">The character name when a source knew it; an ESI fleet member or a fleet mate's sample carries none.</param>
/// <param name="SolarSystemId">The SDE solar system id.</param>
/// <param name="Source">Which source reported it.</param>
/// <param name="ObservedAt">When it was last seen there, so a consumer can show how old it is.</param>
public sealed record FleetPositionDto(int CharacterId, string? Name, int SolarSystemId, PositionSource Source, DateTimeOffset ObservedAt);
