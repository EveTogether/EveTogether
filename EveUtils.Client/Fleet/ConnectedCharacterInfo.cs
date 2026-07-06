namespace EveUtils.Client.Fleet;

/// <summary>A character currently connected to the server (gRPC <c>ConnectedCharacterDto</c>) — the invite pick source.</summary>
public sealed record ConnectedCharacterInfo(int CharacterId, string CharacterName);
