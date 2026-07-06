namespace EveUtils.Client.LocalApi.Dtos;

/// <summary>One fitted item/slot: type id, resolved type name, the ESI slot flag and quantity.</summary>
public sealed record FitItemDto(int TypeId, string TypeName, string Flag, int Quantity);
