using System.Security.Cryptography;

namespace EveUtils.Server.Backup;

// EveUtils.Server.Stream (the DPS hub) shadows System.IO.Stream inside this project.
using Stream = System.IO.Stream;

/// <summary>
/// Passes writes through to the archive entry while hashing them. The manifest's per-file checksum is therefore
/// taken from the bytes that were actually written, in one pass — the export never has to hold an entry in memory
/// or read it back to describe it.
/// </summary>
internal sealed class BackupHashingStream(Stream inner) : Stream
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

    public long BytesWritten { get; private set; }

    /// <summary>Lowercase hex SHA-256 of everything written so far. Reading it finalises the hash.</summary>
    public string Digest() => Convert.ToHexStringLower(_hash.GetCurrentHash());

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        _hash.AppendData(buffer);
        inner.Write(buffer);
        BytesWritten += buffer.Length;
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void WriteByte(byte value) => Write([value]);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Write(buffer.Span);
        return ValueTask.CompletedTask;
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush() => inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _hash.Dispose();

        base.Dispose(disposing);
    }
}
