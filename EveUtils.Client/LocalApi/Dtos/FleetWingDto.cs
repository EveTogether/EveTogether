using System.Collections.Generic;

namespace EveUtils.Client.LocalApi.Dtos;

/// <summary>A wing of the active fleet with its squads (its members reference it by <c>WingId</c>).</summary>
public sealed record FleetWingDto(long Id, string Name, IReadOnlyList<FleetSquadDto> Squads);
