namespace EveUtils.Shared.Modules.Sde.Dtos;

/// <summary>A top-level inventory category (e.g. "Ship", "Module", "Charge").</summary>
public sealed record SdeCategory(int CategoryId, string Name, bool Published);
