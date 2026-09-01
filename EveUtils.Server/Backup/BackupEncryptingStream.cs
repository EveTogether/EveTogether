using System.Security.Cryptography;

namespace EveUtils.Server.Backup;

// EveUtils.Server.Stream (the DPS hub) shadows System.IO.Stream inside this project.
using Stream = System.IO.Stream;

/// <summary>
/// Write side of <see cref="BackupEnvelope"/>: fills a chunk buffer, seals each full chunk with AES-256-GCM and
/// writes the last one — the only chunk carrying the final flag — on dispose. Constant memory regardless of how
/// large the archive is.
/// </summary>
internal sealed class BackupEncryptingStream(
    Stream destination,
    byte[] key,
    byte[] noncePrefix,
    byte[] headerBinding,
    int chunkSize,
    long headerBytes) : Stream
{
    private readonly byte[] _buffer = new byte[chunkSize];
    private int _buffered;
    private uint _chunkIndex;
    private bool _finished;

    /// <summary>Bytes handed to the destination, header and framing included — the size of the finished file.
    /// Only complete once the stream has been disposed and the final chunk written.</summary>
    public long BytesWritten { get; private set; } = headerBytes;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_finished, this);

        while (!buffer.IsEmpty)
        {
            var take = Math.Min(_buffer.Length - _buffered, buffer.Length);
            buffer[..take].CopyTo(_buffer.AsSpan(_buffered));
            _buffered += take;
            buffer = buffer[take..];

            // Only seal a full chunk here. The tail stays buffered until dispose, so the final flag always lands
            // on a chunk that is genuinely last — even when the payload is an exact multiple of the chunk size.
            if (_buffered == _buffer.Length)
                _WriteChunk(isFinal: false);
        }
    }

    public override void Write(byte[] buffer, int offset, int count) =>
        Write(buffer.AsSpan(offset, count));

    public override void WriteByte(byte value) => Write([value]);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Write(buffer.Span);
        return ValueTask.CompletedTask;
    }

    public override void Flush() => destination.Flush();

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_finished)
            _WriteChunk(isFinal: true);

        base.Dispose(disposing);
    }

    private void _WriteChunk(bool isFinal)
    {
        var plaintext = _buffer.AsSpan(0, _buffered);
        var chunkHeader = BackupEnvelope.ChunkHeader(isFinal, _buffered);
        var cipher = new byte[_buffered];
        var tag = new byte[BackupEnvelope.TagSize];

        using (var aes = new AesGcm(key, BackupEnvelope.TagSize))
        {
            aes.Encrypt(BackupEnvelope.Nonce(noncePrefix, _chunkIndex, isFinal), plaintext, cipher, tag,
                BackupEnvelope.AssociatedData(headerBinding, chunkHeader));
        }

        destination.Write(chunkHeader);
        destination.Write(cipher);
        destination.Write(tag);
        BytesWritten += chunkHeader.Length + cipher.Length + tag.Length;

        _buffered = 0;
        _chunkIndex++;
        _finished = isFinal;
    }
}
