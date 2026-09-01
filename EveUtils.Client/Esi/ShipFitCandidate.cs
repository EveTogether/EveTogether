namespace EveUtils.Client.Esi;

/// <summary>A known local fit considered for the active ship.</summary>
public sealed record ShipFitCandidate(int Id, string Name, int ShipTypeId);
