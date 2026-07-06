namespace EveUtils.Shared.Modules.Sde.Dtos;

/// <summary>An inventory group (e.g. "Frigate", "Energy Weapon") linking types to a category.</summary>
public sealed record SdeGroup(int GroupId, int CategoryId, string Name, bool Published);
