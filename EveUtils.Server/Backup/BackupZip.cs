using System.Security.Cryptography;
using ICSharpCode.SharpZipLib.Zip;

namespace EveUtils.Server.Backup;

// EveUtils.Server.Stream (the DPS hub) shadows System.IO.Stream inside this project.
using Stream = System.IO.Stream;

/// <summary>
/// The archive's encryption, which is the ZIP's own: WinZip AES-256 (ET-102). An admin can open the file with
/// 7-Zip, WinRAR or any recent <c>unzip</c>, which is the whole reason this replaced the container ET-99 wrapped
/// around a plain ZIP — that container was safe and nothing on earth could read it.
///
/// The price is the key derivation. The WinZip AE-2 specification fixes it at PBKDF2-HMAC-SHA1 with 1000
/// iterations — not a choice made here, and raising it is precisely what would make the file unreadable to those
/// programs again. Nothing here can compensate for that: the only thing that still raises the cost of guessing
/// offline is password length, which is why <see cref="BackupPasswordPolicy"/> asks for so much more of it than a
/// panel login does.
/// </summary>
internal static class BackupZip
{
    public const int KeySize = 256;

    /// <summary>
    /// Starts writing an encrypted archive onto <paramref name="destination"/>, which need not be seekable — a
    /// database streams out through this straight into the HTTP response. The caller disposes the returned stream;
    /// that is what writes the central directory, and a file without one is not a ZIP to anything.
    /// </summary>
    public static ZipOutputStream CreateWriter(Stream destination, string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);

        return new ZipOutputStream(destination) { Password = password, IsStreamOwner = false };
    }

    /// <summary>Header for one entry. The timestamp is the archive's, not the clock's, so the entries of one
    /// archive agree with each other and with the manifest when an admin looks at them in 7-Zip.</summary>
    public static ZipEntry Entry(string name, DateTimeOffset createdAt) =>
        new(name) { AESKeySize = KeySize, DateTime = createdAt.UtcDateTime };

    /// <summary>
    /// Opens an archive for reading. <paramref name="source"/> must be seekable: a ZIP is read from its central
    /// directory backwards, which is also what makes a truncated file fail here — loudly, and before any of it is
    /// believed — instead of halfway through a restore.
    /// </summary>
    public static ZipFile OpenReader(Stream source, string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);

        try
        {
            return new ZipFile(source, leaveOpen: true) { Password = password };
        }
        catch (Exception ex) when (ex is ZipException or EndOfStreamException)
        {
            throw new InvalidDataException(
                "This file is not a readable ZIP archive. It is damaged, it was cut short on the way here, or it " +
                "was never an EVE Together backup.", ex);
        }
    }

    /// <summary>
    /// Opens the manifest, and with it proves the password: every AES entry carries a two-byte password verifier,
    /// so the wrong password fails on the first entry anything reads rather than somewhere inside the restore. The
    /// failure is a <see cref="CryptographicException"/> because the caller answers it the way ET-99 already did —
    /// a wrong password and an altered archive get the same message, deliberately.
    /// </summary>
    public static Stream OpenManifest(ZipFile zip)
    {
        var entry = zip.GetEntry(BackupFormat.ManifestEntry)
            ?? throw new InvalidDataException("The archive has no manifest, so it is not an EVE Together backup.");

        try
        {
            return zip.GetInputStream(entry);
        }
        catch (ZipException ex)
        {
            throw new CryptographicException("The archive's manifest could not be decrypted.", ex);
        }
    }

    /// <summary>
    /// Opens an entry the manifest names. The password is already proven by the time anything calls this, so what
    /// is left to go wrong is the entry itself — and SharpZipLib checks its AES authentication code as the last
    /// bytes come out, which is why every entry is read in full before the restore starts on anything.
    /// </summary>
    public static Stream OpenEntry(ZipFile zip, string entryName, string description)
    {
        var entry = zip.GetEntry(entryName)
            ?? throw new InvalidDataException(
                $"The backup archive is incomplete: it lists {description} but does not contain '{entryName}'.");

        return zip.GetInputStream(entry);
    }
}
