namespace EveUtils.Shared.Modules.Fleet.Dtos;

/// <summary>A fleet mate's first connection to the server came up, or their last one went away (ET-492).</summary>
public sealed record FleetMateConnectionPayload(int CharacterId, bool IsConnected);
