namespace EveUtils.Client.LocalApi.Dtos;

/// <summary>A squad within a wing of the active fleet (its members reference it by <c>SquadId</c>).</summary>
public sealed record FleetSquadDto(long Id, string Name);
