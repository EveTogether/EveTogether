using System.Security.Cryptography;
using System.Text;

namespace EveUtils.Shared.Modules.ApiKeys.Services;

/// <summary>The key that was just minted. <see cref="PlainText"/> is the only moment it exists in the clear.</summary>
public sealed record GeneratedApiKey(string Prefix, string Secret, string PlainText);

/// <summary>
/// Key format, generation and verification for the server REST API: <c>evek_&lt;prefix&gt;_&lt;secret&gt;</c>.
/// The secret is 256 bits of randomness, so a plain SHA-256 is the right store — no slow KDF, which only buys
/// anything against guessable passwords.
/// </summary>
public static class ApiKeySecurity
{
    private const string Marker = "evek";

    /// <summary>Hex, so it never collides with the base64url secret's '_' when the key is split.</summary>
    private const int PrefixBytes = 4;
    private const int SecretBytes = 32;

    public static GeneratedApiKey Generate()
    {
        var prefix = Convert.ToHexString(RandomNumberGenerator.GetBytes(PrefixBytes)).ToLowerInvariant();
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(SecretBytes))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return new GeneratedApiKey(prefix, secret, $"{Marker}_{prefix}_{secret}");
    }

    public static string Hash(string secret) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    /// <summary>
    /// Constant-time comparison of the presented secret against the stored hash. Never replace this with an
    /// ordinary string comparison: that returns on the first differing character and leaks the hash byte by byte
    /// to an attacker who can time the responses.
    /// </summary>
    public static bool Verify(string secret, string storedHash) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(Hash(secret)), Encoding.UTF8.GetBytes(storedHash));

    /// <summary>Splits a presented key into its lookup prefix and secret. False = not this key format.</summary>
    public static bool TryParse(string? key, out string prefix, out string secret)
    {
        prefix = string.Empty;
        secret = string.Empty;
        if (string.IsNullOrWhiteSpace(key))
            return false;

        // Limit 3: the secret is base64url and may itself contain '_'.
        var parts = key.Split('_', 3);
        if (parts.Length != 3 || parts[0] != Marker || parts[1].Length == 0 || parts[2].Length == 0)
            return false;

        prefix = parts[1];
        secret = parts[2];
        return true;
    }
}
