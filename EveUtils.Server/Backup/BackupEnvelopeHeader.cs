using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EveUtils.Server.Backup;

/// <summary>
/// The cleartext preamble of a <c>.etbackup</c> file: what a reader needs before it can derive the key. It carries
/// no secret — only the KDF parameters and the salt — and it is bound into every chunk's associated data, so an
/// edit to it fails authentication instead of silently changing how the file is read.
/// </summary>
internal sealed class BackupEnvelopeHeader
{
    public const string ExpectedMagic = "ETBACKUP";

    /// <summary>Crypto framing version. Separate from <see cref="BackupFormat.ContentVersion"/>, which versions
    /// what is inside the envelope.</summary>
    public const int CurrentVersion = 1;

    public const string Pbkdf2Sha256 = "pbkdf2-sha256";

    [JsonPropertyName("magic")] public string Magic { get; set; } = ExpectedMagic;
    [JsonPropertyName("formatVersion")] public int FormatVersion { get; set; } = CurrentVersion;
    [JsonPropertyName("kdf")] public string Kdf { get; set; } = Pbkdf2Sha256;
    [JsonPropertyName("iterations")] public int Iterations { get; set; }
    [JsonPropertyName("salt")] public string Salt { get; set; } = string.Empty;
    [JsonPropertyName("noncePrefix")] public string NoncePrefix { get; set; } = string.Empty;
    [JsonPropertyName("chunkSize")] public int ChunkSize { get; set; }

    public static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    public byte[] ToBytes() => JsonSerializer.SerializeToUtf8Bytes(this, SerializerOptions);

    /// <summary>Parses and range-checks a header. Returns null when the bytes are not a header this build
    /// understands — the caller turns that into "this is not an EVE Together backup", without spending the KDF.</summary>
    public static BackupEnvelopeHeader? TryParse(ReadOnlySpan<byte> utf8)
    {
        BackupEnvelopeHeader? header;
        try
        {
            header = JsonSerializer.Deserialize<BackupEnvelopeHeader>(utf8, SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        if (header is null || !string.Equals(header.Magic, ExpectedMagic, StringComparison.Ordinal))
            return null;
        if (header.FormatVersion != CurrentVersion || !string.Equals(header.Kdf, Pbkdf2Sha256, StringComparison.Ordinal))
            return null;
        // Bounded so a hostile header cannot make this build burn CPU or allocate a chunk buffer of its choosing.
        if (header.Iterations is < 100_000 or > 5_000_000)
            return null;
        if (header.ChunkSize is < 4096 or > 16 * 1024 * 1024)
            return null;

        return _HasValidBinaryFields(header) ? header : null;
    }

    public byte[] SaltBytes() => Convert.FromBase64String(Salt);

    public byte[] NoncePrefixBytes() => Convert.FromBase64String(NoncePrefix);

    private static bool _HasValidBinaryFields(BackupEnvelopeHeader header)
    {
        try
        {
            return Convert.FromBase64String(header.Salt).Length == BackupEnvelope.SaltSize
                && Convert.FromBase64String(header.NoncePrefix).Length == BackupEnvelope.NoncePrefixSize;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public override string ToString() => Encoding.UTF8.GetString(ToBytes());
}
