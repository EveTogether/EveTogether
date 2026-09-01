using System.Security.Cryptography;

namespace EveUtils.Server.Backup;

/// <summary>
/// Minimum length for the password that encrypts an archive, and the generator that offers to meet it.
///
/// The number is what it is because of the format. WinZip AES derives its key with PBKDF2-HMAC-SHA1 at 1000
/// iterations — fixed by the specification, and raising it is exactly what would stop 7-Zip from opening the file
/// (ET-102). So the work factor per guess is roughly nothing, and the only thing left standing between a stolen
/// archive and every refresh token in it is the number of guesses. That is length, and nothing else.
/// </summary>
internal static class BackupPasswordPolicy
{
    public const int MinLength = 20;

    /// <summary>Length of a generated password. Longer than the minimum on purpose: nobody types this one, so
    /// there is no reason to spend its strength on being memorable.</summary>
    public const int GeneratedLength = 32;

    public const string Requirement =
        "The archive password must be at least 20 characters. The ZIP format derives its key with only 1000 " +
        "PBKDF2 rounds, so the strength of this archive is the length of this password and nothing else — use a " +
        "long passphrase or the generator. It cannot be recovered: without it the archive is permanently " +
        "unreadable, and with it anyone can take over every linked character.";

    public static bool IsValid(string? password) =>
        !string.IsNullOrEmpty(password) && password.Length >= MinLength;

    /// <summary>
    /// A password nobody has to remember, for the admin who would otherwise pick one that is merely long. Shown
    /// once by the panel and kept nowhere: there is no recovery path for an archive, by design.
    /// </summary>
    public static string Generate() => RandomNumberGenerator.GetString(Alphabet, GeneratedLength);

    /// <summary>Letters and digits minus the shapes that get misread when a password is copied by hand — no
    /// <c>I</c>, <c>l</c>, <c>O</c>, <c>0</c> or <c>1</c>. 32 characters out of these 57 is about 186 bits, which
    /// is past anything the format's 1000-round key derivation gives away.</summary>
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
}
