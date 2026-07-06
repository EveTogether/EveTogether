namespace EveUtils.Shared.Modules.Ships.Dtos;

/// <summary>Public contract of the Ships module (UI/gRPC). No EF entity goes out.</summary>
public record ShipDto(int Id, string Name, string Class, decimal Mass);
