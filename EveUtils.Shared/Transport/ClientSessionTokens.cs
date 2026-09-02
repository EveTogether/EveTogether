namespace EveUtils.Shared.Transport;

/// <summary>
/// The server-issued session the client holds after pairing — its credential for gRPC.
/// <para><paramref name="ServerSessionId"/> is the server's own id for this session, kept because it outlives the
/// rotations the tokens do not. Presenting it on a refresh is what lets the server say whether a refusal means
/// "your copy is stale" or "this session is gone" (ET-123). 0 means not known yet — paired before the server
/// handed it out — and is filled in by the first heartbeat that gets through.</para>
/// </summary>
public sealed record ClientSessionTokens(
    string AccessToken, string RefreshToken, string CharacterName, int CharacterId, int ServerSessionId = 0);
