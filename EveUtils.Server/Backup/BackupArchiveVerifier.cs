using System.Security.Cryptography;
using ICSharpCode.SharpZipLib.Zip;

namespace EveUtils.Server.Backup;

/// <summary>
/// Checks every entry the manifest names against its recorded SHA-256, in full, before the restore drops
/// anything. The ticket's requirement in one place: a damaged or half-uploaded archive fails loudly instead of
/// leaving half a restore behind. Reading each entry to its last byte is also what makes SharpZipLib check the
/// AES authentication code on it, so an edited ciphertext is caught here as well as an edited plaintext.
/// </summary>
internal static class BackupArchiveVerifier
{
    public static void Verify(ZipFile zip, BackupManifest manifest)
    {
        foreach (var table in manifest.Tables)
            _VerifyEntry(zip, table.Entry, table.Sha256, $"table '{table.Name}'");

        foreach (var file in manifest.Files)
        {
            _VerifyEntry(zip, file.Entry, file.Sha256, $"file '{file.Name}'");

            // The archive decides content, never location: a name with a path in it would let an uploaded file be
            // written outside the data directory. Only the known identity files are ever written back anyway
            // (ServerBackupOptions.ArchivedFiles), and this refuses the attempt rather than quietly ignoring it.
            if (!string.Equals(Path.GetFileName(file.Name), file.Name, StringComparison.Ordinal))
                throw new InvalidDataException($"The backup archive names a file with a path in it ('{file.Name}').");
        }
    }

    private static void _VerifyEntry(ZipFile zip, string entryName, string expected, string description)
    {
        using var stream = BackupZip.OpenEntry(zip, entryName, description);
        var actual = Convert.ToHexStringLower(SHA256.HashData(stream));
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"The backup archive is damaged: the contents of {description} do not match the checksum in its manifest.");
        }
    }
}
