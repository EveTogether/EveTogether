using System.Security.Cryptography;
using System.Text;

namespace EveUtils.Client.Opsec;

/// <summary>
/// Turns a location into unreadable glyphs (ET-417). The glyphs come from a keyed hash of the whole value, never from
/// its letters, so there is no letter mapping to recover by frequency analysis, and the length says nothing about the
/// name. The key is drawn fresh per session: within one session a name keeps its mask (no flicker on redraw), the next
/// session it gets a different one.
/// </summary>
public sealed class OpsecMask(byte[] key)
{
    public const int MinLength = 4;
    public const int MaxLength = 12;

    // Noto Sans Old Turkic's block, minus its first four glyphs: single plain strokes that read as I or Y.
    public const int FirstGlyph = 0x10C04;
    public const int LastGlyph = 0x10C48;

    // Isolates the right-to-left glyphs, so a mask inside "site · system" cannot reorder the text around it.
    private const char IsolateStart = '⁦';
    private const char IsolateEnd = '⁩';

    public static OpsecMask ForThisSession() => new(RandomNumberGenerator.GetBytes(32));

    public string Mask(string location)
    {
        byte[] hash = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(location));
        var length = MinLength + hash[0] % (MaxLength - MinLength + 1);
        const int glyphCount = LastGlyph - FirstGlyph + 1;

        var masked = new StringBuilder(2 + length * 2);
        masked.Append(IsolateStart);
        for (var i = 1; i <= length; i++)
            masked.Append(char.ConvertFromUtf32(FirstGlyph + hash[i] % glyphCount));
        masked.Append(IsolateEnd);
        return masked.ToString();
    }
}
