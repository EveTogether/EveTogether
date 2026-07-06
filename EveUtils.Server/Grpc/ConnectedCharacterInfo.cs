namespace EveUtils.Server.Grpc;

/// <summary>A distinct connected character (id + name) — the source for invite discovery.</summary>
public sealed record ConnectedCharacterInfo(int CharacterId, string CharacterName);
