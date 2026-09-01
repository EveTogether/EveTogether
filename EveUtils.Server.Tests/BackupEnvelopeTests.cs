using System.Security.Cryptography;
using EveUtils.Server.Backup;
using Xunit;

namespace EveUtils.Server.Tests;

/// <summary>
/// The encrypted container an archive lives in (ET-99). The archive decrypts every stored refresh token, so the
/// failure paths matter more than the happy one: a wrong password, an edited byte and — the case the ticket calls
/// out — a download or upload that stopped halfway must all refuse, never half-succeed.
/// </summary>
public class BackupEnvelopeTests
{
    private const string Password = "a-sufficiently-long-passphrase";
    private const int ChunkSize = 4096;

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(ChunkSize - 1)]
    [InlineData(ChunkSize)]          // exactly one full chunk: the final chunk is the empty one
    [InlineData(ChunkSize + 1)]
    [InlineData(ChunkSize * 3)]
    public void RoundTrip_AnyLength_ReturnsTheSameBytes(int length)
    {
        var payload = RandomNumberGenerator.GetBytes(length);

        var decrypted = Decrypt(Encrypt(payload), Password);

        Assert.Equal(payload, decrypted);
    }

    [Fact]
    public void OpenReader_WrongPassword_Throws()
    {
        var archive = Encrypt(RandomNumberGenerator.GetBytes(ChunkSize * 2));

        Assert.ThrowsAny<CryptographicException>(() => Decrypt(archive, "a-different-long-passphrase"));
    }

    /// <summary>
    /// The regression this format exists for: an interrupted transfer leaves a file that decrypts perfectly up to
    /// where it stops. Only the final chunk's flag — which is part of its nonce, so it cannot be forged onto an
    /// earlier chunk — says the archive is whole.
    /// </summary>
    [Fact]
    public void OpenReader_TruncatedArchive_ThrowsInsteadOfReturningAShortStream()
    {
        var archive = Encrypt(RandomNumberGenerator.GetBytes(ChunkSize * 3));

        var truncated = archive[..(archive.Length - ChunkSize)];

        var error = Assert.Throws<InvalidDataException>(() => Decrypt(truncated, Password));
        Assert.Contains("truncated", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OpenReader_TamperedCiphertext_Throws()
    {
        var archive = Encrypt(RandomNumberGenerator.GetBytes(ChunkSize * 2));
        archive[^40] ^= 0xFF;

        Assert.ThrowsAny<CryptographicException>(() => Decrypt(archive, Password));
    }

    /// <summary>
    /// The header is cleartext, so it is bound into every chunk's associated data. The salt and the chunk size
    /// would give themselves away by changing the key or the framing; this rewrites the header in a way that parses
    /// to exactly the same values — added whitespace — and it still has to fail.
    /// </summary>
    [Fact]
    public void OpenReader_HeaderRewrittenWithoutChangingItsValues_StillThrows()
    {
        var archive = Encrypt(RandomNumberGenerator.GetBytes(ChunkSize));
        var header = System.Text.Encoding.UTF8.GetString(archive, 4, BitConverter.ToInt32(archive, 0));
        var edited = header.Replace("{\"magic\"", "{ \"magic\"", StringComparison.Ordinal);
        Assert.NotEqual(header, edited); // the edit has to actually land, or this test proves nothing

        var tampered = Rebuild(archive, edited);

        Assert.ThrowsAny<CryptographicException>(() => Decrypt(tampered, Password));
    }

    [Fact]
    public void OpenReader_NotABackupArchive_SaysSoWithoutSpendingTheKdf()
    {
        var notAnArchive = new MemoryStream(RandomNumberGenerator.GetBytes(512));

        Assert.Throws<InvalidDataException>(() => BackupEnvelope.OpenReader(notAnArchive, Password));
    }

    private static byte[] Encrypt(byte[] payload)
    {
        var destination = new MemoryStream();
        using (var envelope = BackupEnvelope.CreateWriter(destination, Password, ChunkSize))
            envelope.Write(payload);

        return destination.ToArray();
    }

    private static byte[] Decrypt(byte[] archive, string password)
    {
        using var plaintext = BackupEnvelope.OpenReader(new MemoryStream(archive), password);
        var result = new MemoryStream();
        plaintext.CopyTo(result);
        return result.ToArray();
    }

    private static byte[] Rebuild(byte[] archive, string header)
    {
        var headerBytes = System.Text.Encoding.UTF8.GetBytes(header);
        var body = archive[(4 + BitConverter.ToInt32(archive, 0))..];
        return [.. BitConverter.GetBytes(headerBytes.Length), .. headerBytes, .. body];
    }
}
