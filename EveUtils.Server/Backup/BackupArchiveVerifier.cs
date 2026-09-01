using System.IO.Compression;
using System.Security.Cryptography;

namespace EveUtils.Server.Backup;

/// <summary>
/// Checks every entry the manifest names against its recorded SHA-256, in full, before the restore drops
/// anything. The ticket's requirement in one place: a damaged or half-uploaded archive fails loudly instead of
/// leaving half a restore behind. The envelope already catches a truncated file; this catches an entry that
/// decrypts cleanly but does not hold what the manifest says it does.
/// </summary>
internal static class BackupArchiveVerifier
{
    public static void Verify(ZipArchive zip, BackupManifest manifest)
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

    private static void _VerifyEntry(ZipArchive zip, string entryName, string expected, string description)
    {
        var entry = zip.GetEntry(entryName)
            ?? throw new InvalidDataException(
                $"The backup archive is incomplete: it lists {description} but does not contain '{entryName}'.");

        using var stream = entry.Open();
        var actual = Convert.ToHexStringLower(SHA256.HashData(stream));
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"The backup archive is damaged: the contents of {description} do not match the checksum in its manifest.");
        }
    }
}
