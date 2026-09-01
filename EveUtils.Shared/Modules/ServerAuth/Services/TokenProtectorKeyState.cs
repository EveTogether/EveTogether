namespace EveUtils.Shared.Modules.ServerAuth.Services;

/// <summary>
/// Whether the token-protector key was generated at startup rather than loaded from the data directory. A generated
/// key cannot decrypt any <c>SyncedCharacter.RefreshTokenCipher</c> written under the previous one, so the server
/// checks this against the paired characters once the database is reachable (ET-94).
/// </summary>
public sealed record TokenProtectorKeyState(bool KeyWasCreated, string KeyPath);
