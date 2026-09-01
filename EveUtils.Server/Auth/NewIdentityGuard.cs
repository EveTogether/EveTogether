namespace EveUtils.Server.Auth;

/// <summary>
/// Startup gate on a freshly generated token-protector key (ET-94). The key decrypts
/// <c>SyncedCharacter.RefreshTokenCipher</c>; a new one next to characters that were paired under the previous key
/// makes those refresh tokens unreadable for good, so the server refuses to start rather than logging and carrying
/// on — the incident this guards against was only noticed once clients started failing.
/// </summary>
internal static class NewIdentityGuard
{
    public const string AcceptSwitch = "--accept-new-identity";
    public const string AcceptConfigurationKey = "Server:AcceptNewIdentity";

    /// <summary>
    /// A key that was loaded is fine, and a generated key with no paired characters is just a first start — only
    /// the combination is the failure, and only until it is explicitly accepted.
    /// </summary>
    public static bool ShouldRefuseStart(bool keyWasCreated, int syncedCharacterCount, bool newIdentityAccepted) =>
        keyWasCreated && syncedCharacterCount > 0 && !newIdentityAccepted;

    /// <summary>
    /// Removes the key this start generated, after refusing over it. Leaving it there would make the guard fire
    /// exactly once: the next start would find a key, load it, and come up on refresh tokens it cannot decrypt.
    /// Deleting it also lets the matching key from backup drop straight into place.
    /// </summary>
    public static void DiscardGeneratedKey(string keyPath) => File.Delete(keyPath);

    public static string RefusalMessage(string dataDirectory, int syncedCharacterCount) =>
        $"Refusing to start: a new token-protector key was generated in '{dataDirectory}', but {syncedCharacterCount} " +
        "character(s) are already paired. Their stored ESI refresh tokens were encrypted with the previous key and " +
        "cannot be decrypted with this one. Restore token-protector.key from the backup that goes with this database " +
        $"(they belong together), or pass {AcceptSwitch} / set {AcceptConfigurationKey}=true to accept the new " +
        "identity and re-pair every character.";
}
