namespace EveUtils.Server.Grpc;

/// <summary>Presence snapshot of one attached client — shown in the admin panel.</summary>
public sealed record ConnectedClientInfo(string CharacterName, DateTimeOffset ConnectedAt);
