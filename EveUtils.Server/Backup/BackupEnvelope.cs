using System.Buffers.Binary;
using System.Security.Cryptography;

namespace EveUtils.Server.Backup;

// EveUtils.Server.Stream (the DPS hub) shadows System.IO.Stream inside this project.
using Stream = System.IO.Stream;

/// <summary>
/// Password-based encryption for a backup archive. The archive is the one file that decrypts every stored refresh
/// token, so it is never written in the clear (ET-99).
///
/// Layout: <c>[4-byte LE header length][header JSON][chunk]…</c>, each chunk
/// <c>[1-byte final flag][4-byte LE ciphertext length][ciphertext][16-byte GCM tag]</c>.
/// Every chunk is AES-256-GCM under a key derived once with PBKDF2-HMAC-SHA256 — the same KDF the panel already
/// uses for admin passwords (<c>Pbkdf2AdminPasswordHasher</c>) — with the chunk index in the nonce.
///
/// Chunking rather than one AES-GCM pass over the whole file is what lets a multi-gigabyte database stream in
/// constant memory. It also buys the property the ticket asks for: exactly one chunk is marked final, and its flag
/// is part of the nonce, so an archive that was truncated mid-download hits the end of the stream without ever
/// having seen that chunk and fails loudly instead of restoring half a server.
/// </summary>
internal static class BackupEnvelope
{
    public const int SaltSize = 16;
    public const int NoncePrefixSize = 7;   // + 4-byte counter + 1-byte final flag = the 12-byte GCM nonce
    public const int TagSize = 16;
    public const int KeySize = 32;
    public const int ChunkHeaderSize = 5;   // final flag + ciphertext length

    /// <summary>One-off cost on a deliberate admin action, so an order of magnitude above the login hasher's
    /// 210k. Bounded on read by <see cref="BackupEnvelopeHeader.TryParse"/>.</summary>
    public const int Iterations = 600_000;

    public const int DefaultChunkSize = 1024 * 1024;

    /// <summary>Wraps <paramref name="destination"/> in a write-only stream that encrypts what is written to it.
    /// The caller must dispose the returned stream: that is what writes the final chunk, and an archive without
    /// one is rejected on read.</summary>
    public static BackupEncryptingStream CreateWriter(Stream destination, string password, int chunkSize = DefaultChunkSize)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);

        var header = new BackupEnvelopeHeader
        {
            Iterations = Iterations,
            Salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(SaltSize)),
            NoncePrefix = Convert.ToBase64String(RandomNumberGenerator.GetBytes(NoncePrefixSize)),
            ChunkSize = chunkSize,
        };

        var headerBytes = header.ToBytes();
        var length = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, headerBytes.Length);
        destination.Write(length);
        destination.Write(headerBytes);

        return new BackupEncryptingStream(destination, DeriveKey(password, header.SaltBytes(), header.Iterations),
            header.NoncePrefixBytes(), HeaderBinding(headerBytes), chunkSize, length.Length + headerBytes.Length);
    }

    /// <summary>Wraps <paramref name="source"/> in a read-only stream that decrypts it. Throws
    /// <see cref="InvalidDataException"/> when the bytes are not a backup archive this build understands, and
    /// <see cref="CryptographicException"/> when the password is wrong or the archive has been altered.</summary>
    public static Stream OpenReader(Stream source, string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);

        var length = new byte[sizeof(int)];
        source.ReadExactly(length);
        var headerLength = BinaryPrimitives.ReadInt32LittleEndian(length);
        if (headerLength is < 2 or > 4096)
            throw new InvalidDataException("This is not an EVE Together backup archive.");

        var headerBytes = new byte[headerLength];
        source.ReadExactly(headerBytes);

        var header = BackupEnvelopeHeader.TryParse(headerBytes)
            ?? throw new InvalidDataException(
                "This is not an EVE Together backup archive, or it was written by a newer server than this one.");

        return new BackupDecryptingStream(source, DeriveKey(password, header.SaltBytes(), header.Iterations),
            header.NoncePrefixBytes(), HeaderBinding(headerBytes), header.ChunkSize);
    }

    public static byte[] DeriveKey(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, KeySize);

    /// <summary>The header digest that prefixes every chunk's associated data. The salt and the chunk size already
    /// change the key or the framing if edited; binding the whole header covers the rest of it too, so no field
    /// needs to be reasoned about separately.</summary>
    public static byte[] HeaderBinding(byte[] headerBytes) => SHA256.HashData(headerBytes);

    /// <summary>Nonce for chunk <paramref name="index"/>: the archive's random prefix, the chunk counter, and the
    /// final flag — so a chunk cannot be replayed at another position or re-labelled as the last one.</summary>
    public static byte[] Nonce(byte[] noncePrefix, uint index, bool isFinal)
    {
        var nonce = new byte[AesGcm.NonceByteSizes.MaxSize];
        noncePrefix.CopyTo(nonce, 0);
        BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(NoncePrefixSize, sizeof(uint)), index);
        nonce[^1] = isFinal ? (byte)1 : (byte)0;
        return nonce;
    }

    public static byte[] ChunkHeader(bool isFinal, int cipherLength)
    {
        var chunkHeader = new byte[ChunkHeaderSize];
        chunkHeader[0] = isFinal ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt32LittleEndian(chunkHeader.AsSpan(1), cipherLength);
        return chunkHeader;
    }

    /// <summary>Associated data for one chunk: the header digest plus that chunk's own framing, so neither the
    /// file header nor a chunk's length or position can be changed without failing authentication.</summary>
    public static byte[] AssociatedData(byte[] headerBinding, byte[] chunkHeader) =>
        [.. headerBinding, .. chunkHeader];
}
