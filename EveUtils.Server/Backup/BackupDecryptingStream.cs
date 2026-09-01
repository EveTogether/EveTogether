using System.Buffers.Binary;
using System.Security.Cryptography;

namespace EveUtils.Server.Backup;

// EveUtils.Server.Stream (the DPS hub) shadows System.IO.Stream inside this project.
using Stream = System.IO.Stream;

/// <summary>
/// Read side of <see cref="BackupEnvelope"/>. Reads one chunk at a time and serves the plaintext from it.
///
/// Two failure modes matter more than throughput here, and both end in a throw rather than a short read: a wrong
/// password or a tampered byte fails the GCM tag, and an archive whose upload or download was cut short runs out
/// of stream without ever having decrypted a chunk marked final.
/// </summary>
internal sealed class BackupDecryptingStream(
    Stream source,
    byte[] key,
    byte[] noncePrefix,
    byte[] headerBinding,
    int chunkSize) : Stream
{
    private byte[] _plaintext = [];
    private int _offset;
    private uint _chunkIndex;
    private bool _sawFinalChunk;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(Span<byte> buffer)
    {
        // Loop rather than a single refill: an empty block is not an end of stream, and treating it as one would
        // hand the caller a silently short archive.
        while (_offset == _plaintext.Length)
        {
            if (!_ReadChunk())
                return 0;
        }

        var take = Math.Min(buffer.Length, _plaintext.Length - _offset);
        _plaintext.AsSpan(_offset, take).CopyTo(buffer);
        _offset += take;
        return take;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Read(buffer.Span));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <summary>Decrypts the next chunk into the buffer. False once the final chunk has been consumed; throws when
    /// the stream ends before that chunk ever arrives.</summary>
    private bool _ReadChunk()
    {
        if (_sawFinalChunk)
            return false;

        var chunkHeader = new byte[BackupEnvelope.ChunkHeaderSize];
        if (source.ReadAtLeast(chunkHeader, chunkHeader.Length, throwOnEndOfStream: false) < chunkHeader.Length)
            throw _Truncated();

        var isFinal = chunkHeader[0] == 1;
        var cipherLength = BinaryPrimitives.ReadInt32LittleEndian(chunkHeader.AsSpan(1));
        if (cipherLength < 0 || cipherLength > chunkSize)
            throw new InvalidDataException("The backup archive is damaged: a block declares an impossible length.");

        var cipher = new byte[cipherLength];
        var tag = new byte[BackupEnvelope.TagSize];
        try
        {
            source.ReadExactly(cipher);
            source.ReadExactly(tag);
        }
        catch (EndOfStreamException)
        {
            // Cut mid-block rather than on a boundary. Same cause, same answer — not an I/O problem to retry.
            throw _Truncated();
        }

        var plaintext = new byte[cipherLength];
        using (var aes = new AesGcm(key, BackupEnvelope.TagSize))
        {
            aes.Decrypt(BackupEnvelope.Nonce(noncePrefix, _chunkIndex, isFinal), cipher, tag, plaintext,
                BackupEnvelope.AssociatedData(headerBinding, chunkHeader));
        }

        _plaintext = plaintext;
        _offset = 0;
        _chunkIndex++;
        _sawFinalChunk = isFinal;
        return true;
    }

    private static InvalidDataException _Truncated() => new(
        "The backup archive ends before its final block. It was truncated — most likely an upload or download " +
        "that did not finish. Take a fresh archive rather than restoring this one.");
}
