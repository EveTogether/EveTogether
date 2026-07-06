namespace EveUtils.Shared.Modules.Esi;

/// <summary>An EVE SSO token set. Stored encrypted at rest; the refresh token is rotation-proof.</summary>
public sealed record EsiTokenSet(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt);
