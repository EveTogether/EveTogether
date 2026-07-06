namespace EveUtils.Shared.Transport;

/// <summary>The server-issued session the client holds after pairing — its credential for gRPC.</summary>
public sealed record ClientSessionTokens(string AccessToken, string RefreshToken, string CharacterName, int CharacterId);
