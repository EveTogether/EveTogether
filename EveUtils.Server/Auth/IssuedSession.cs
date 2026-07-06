namespace EveUtils.Server.Auth;

/// <summary>A freshly issued server session: the plaintext tokens handed to the client (stored hashed).</summary>
public sealed record IssuedSession(string AccessToken, string RefreshToken);
