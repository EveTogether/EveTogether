namespace EveUtils.Shared.Modules.Sde.Dtos;

/// <summary>A type id with its display name — used for pickers such as a Tactical Destroyer's stance modes.</summary>
public sealed record SdeNamedType(int TypeId, string Name);
