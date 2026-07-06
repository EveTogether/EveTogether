using System.IO.Compression;
using System.Text;

namespace EveUtils.Shared.Modules.Fittings.Services.Parsers;

/// <summary>
/// The eveship.fit URL transport layer: a fit travels as
/// <c>?fit=&lt;type&gt;:&lt;base64(gzip(payload))&gt;</c>. This handles only that outer layer — gzip + (non-URL-safe)
/// base64, and pulling the <c>type:data</c> out of a full URL or a raw paste. The per-type payload (v3 CSV / EFT)
/// is parsed by <see cref="FitTextImporter"/>.
/// </summary>
public static class EveshipFitCodec
{
    public const string BaseUrl = "https://eveship.fit/?fit=";

    private static readonly string[] KnownTypes = ["v1", "v2", "v3", "eft", "killmail"];

    public static string Compress(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionMode.Compress))
            gzip.Write(bytes, 0, bytes.Length);
        return Convert.ToBase64String(output.ToArray());
    }

    public static string Decompress(string base64)
    {
        // A '+' in the base64 can arrive as a space when the URL went through form-decoding; restore it.
        var data = Convert.FromBase64String(base64.Trim().Replace(' ', '+'));
        using var input = new MemoryStream(data);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>True when the input looks like an eveship.fit link or a raw typed payload (v1/v2/v3/eft/killmail:…).</summary>
    public static bool IsEveshipInput(string input)
    {
        var trimmed = input.TrimStart();
        if (trimmed.Contains("eveship.fit", StringComparison.OrdinalIgnoreCase))
            return true;
        var colon = trimmed.IndexOf(':');
        return colon > 0 && KnownTypes.Contains(trimmed[..colon].ToLowerInvariant());
    }

    /// <summary>
    /// Extracts the <c>type</c> (v1/v2/v3/eft/killmail) and the raw <c>data</c> from a full eveship.fit URL or a
    /// raw <c>type:data</c> paste. Returns false when no known type prefix is present.
    /// </summary>
    public static bool TryExtractPayload(string input, out string type, out string data)
    {
        type = string.Empty;
        data = string.Empty;
        var s = input.Trim();

        if (s.Contains("eveship.fit", StringComparison.OrdinalIgnoreCase))
        {
            // The fit lives in ?fit=… or #fit=…; take everything after the marker up to the next separator.
            var marker = s.IndexOf("fit=", StringComparison.OrdinalIgnoreCase);
            if (marker < 0)
                return false;
            s = s[(marker + 4)..];
            var amp = s.IndexOf('&');
            if (amp >= 0)
                s = s[..amp];
            s = Uri.UnescapeDataString(s);
        }

        var colon = s.IndexOf(':');
        if (colon <= 0)
            return false;
        var candidate = s[..colon].ToLowerInvariant();
        if (!KnownTypes.Contains(candidate))
            return false;
        type = candidate;
        data = s[(colon + 1)..];
        return true;
    }

    public static string BuildUrl(string typedPayload) => BaseUrl + typedPayload;
}
