using System.Security.Cryptography;
using EveUtils.Server.Backup;
using ICSharpCode.SharpZipLib.Zip;
using Xunit;

namespace EveUtils.Server.Tests;

/// <summary>
/// The container an archive lives in (ET-102). Two things have to hold at once: it must be an ordinary
/// AES-encrypted ZIP, because being openable with 7-Zip is the entire point of the change, and it must still
/// refuse a wrong password or a file that was cut short instead of handing back what it managed to read.
/// </summary>
public class BackupZipTests
{
    private const string Password = "a-sufficiently-long-passphrase";

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(64 * 1024)]
    [InlineData(300_000)]
    public void RoundTrip_AnyLength_ReturnsTheSameBytes(int length)
    {
        var payload = RandomNumberGenerator.GetBytes(length);

        var archive = Write(payload);

        using var zip = BackupZip.OpenReader(new MemoryStream(archive), Password);
        using var manifest = BackupZip.OpenManifest(zip);
        var read = new MemoryStream();
        manifest.CopyTo(read);
        Assert.Equal(payload, read.ToArray());
    }

    [Fact]
    public void OpenManifest_WrongPassword_ThrowsInsteadOfReturningRubbish()
    {
        var archive = Write(RandomNumberGenerator.GetBytes(4096));

        using var zip = BackupZip.OpenReader(new MemoryStream(archive), "a-different-long-passphrase");

        Assert.Throws<CryptographicException>(() => BackupZip.OpenManifest(zip));
    }

    /// <summary>
    /// The regression the ET-99 container was built around, and the reason a ZIP can replace it: the central
    /// directory sits at the end of the file, so a transfer that stopped halfway is not a readable archive with
    /// fewer entries — it is not an archive at all.
    /// </summary>
    [Fact]
    public void OpenReader_TruncatedArchive_ThrowsInsteadOfOpeningWhatArrived()
    {
        var archive = Write(RandomNumberGenerator.GetBytes(300_000));

        var truncated = archive[..(archive.Length / 2)];

        Assert.Throws<InvalidDataException>(() => BackupZip.OpenReader(new MemoryStream(truncated), Password));
    }

    /// <summary>An entry whose ciphertext was edited decrypts to rubbish, and the AES authentication code at the
    /// end of it says so — but only once the entry has been read to its last byte, which is why the restore reads
    /// every entry in full before it touches anything.</summary>
    [Fact]
    public void OpenManifest_TamperedCiphertext_ThrowsOnceTheEntryIsReadToItsEnd()
    {
        var archive = Write(RandomNumberGenerator.GetBytes(4096));
        archive[2000] ^= 0xFF;   // inside the entry's ciphertext, well clear of both headers

        using var zip = BackupZip.OpenReader(new MemoryStream(archive), Password);

        Assert.Throws<ZipException>(() =>
        {
            using var manifest = BackupZip.OpenManifest(zip);
            manifest.CopyTo(System.IO.Stream.Null);
        });
    }

    [Fact]
    public void OpenReader_NotAnArchiveAtAll_SaysSo()
    {
        var notAnArchive = new MemoryStream(RandomNumberGenerator.GetBytes(512));

        Assert.Throws<InvalidDataException>(() => BackupZip.OpenReader(notAnArchive, Password));
    }

    private static byte[] Write(byte[] payload)
    {
        var destination = new MemoryStream();
        using (var zip = BackupZip.CreateWriter(destination, Password))
        {
            zip.PutNextEntry(BackupZip.Entry(BackupFormat.ManifestEntry, DateTimeOffset.UnixEpoch));
            zip.Write(payload);
            zip.CloseEntry();
        }

        return destination.ToArray();
    }
}
