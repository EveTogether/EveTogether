namespace EveUtils.Server.Backup;

/// <summary>
/// Minimum length for the password that encrypts an archive. Deliberately longer than
/// <c>PasswordPolicy</c>'s eight characters for panel logins: a login is guessed against a server that can rate-
/// limit and lock out, while an archive is guessed offline, at whatever speed the person holding the file can
/// afford. The PBKDF2 work factor raises the cost per guess; only length raises the number of guesses.
/// </summary>
internal static class BackupPasswordPolicy
{
    public const int MinLength = 16;

    public const string Requirement =
        "The archive password must be at least 16 characters. It cannot be recovered: without it the archive is " +
        "permanently unreadable, and with it anyone can take over every linked character.";

    public static bool IsValid(string? password) =>
        !string.IsNullOrEmpty(password) && password.Length >= MinLength;
}
