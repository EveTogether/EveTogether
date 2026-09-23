namespace EveUtils.Shared.Modules.Runs.Dtos;

/// <summary>One item type's loot over a range, valued at the stored ESI average price — an estimate, not a sale.</summary>
public sealed record BestDropDto(int TypeId, string Name, long Quantity, decimal Value);
