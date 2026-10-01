using System.Globalization;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using EveUtils.Client.Opsec;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>ET-417 acceptance 3: a mask cannot be traced back — the same name masks differently per session, its
/// length says nothing, and there is no letter mapping to break by frequency analysis.</summary>
public sealed class OpsecMaskTests
{
    private static readonly string[] SameLengthNames = ["Jita", "Rens", "Amok", "Tama", "Kino", "Osmo", "Eram", "Vlil"];

    [Fact]
    public void SameName_SameSession_SameMask()
    {
        OpsecMask mask = OpsecMask.ForThisSession();

        Assert.Equal(mask.Mask("Jita"), mask.Mask("Jita"));
    }

    [Fact]
    public void SameName_NextSession_DifferentMask()
    {
        Assert.NotEqual(OpsecMask.ForThisSession().Mask("Jita"), OpsecMask.ForThisSession().Mask("Jita"));
    }

    [Fact]
    public void Mask_HoldsOnlyGlyphsFromTheOpsecFont_NeverTheName()
    {
        string masked = OpsecMask.ForThisSession().Mask("Jita");

        int[] glyphs = _Glyphs(masked);
        Assert.InRange(glyphs.Length, OpsecMask.MinLength, OpsecMask.MaxLength);
        Assert.All(glyphs, glyph => Assert.InRange(glyph, OpsecMask.FirstGlyph, OpsecMask.LastGlyph));
        Assert.DoesNotContain("Jita", masked);
    }

    [Fact]
    public void RepeatedLetter_DoesNotRepeatOneGlyph()
    {
        // A letter substitution would turn "AAAAAAAA" into one glyph eight times over.
        int[] glyphs = _Glyphs(OpsecMask.ForThisSession().Mask("AAAAAAAA"));

        Assert.True(glyphs.Distinct().Count() > 1, $"one glyph repeated: {string.Join(',', glyphs)}");
    }

    [Fact]
    public void SameLengthNames_DoNotShareAMaskLength()
    {
        OpsecMask mask = OpsecMask.ForThisSession();

        int lengths = SameLengthNames.Select(name => _Glyphs(mask.Mask(name)).Length).Distinct().Count();

        Assert.True(lengths > 1, "eight names of four letters all masked to one length");
    }

    [Fact]
    public void Mask_IsIsolated_SoItCannotReorderTheTextAroundIt()
    {
        string masked = OpsecMask.ForThisSession().Mask("Jita");

        Assert.Equal('⁦', masked[0]);
        Assert.Equal('⁩', masked[^1]);
    }

    [AvaloniaFact]
    public void MaskGlyphs_AreDrawnInTheShippedOpsecFont()
    {
        bool matched = FontManager.Current.TryMatchCharacter(OpsecMask.FirstGlyph, FontStyle.Normal, FontWeight.Normal,
            FontStretch.Normal, null, CultureInfo.InvariantCulture, out Typeface typeface);

        Assert.True(matched);
        Assert.Equal("Noto Sans Old Turkic", typeface.FontFamily.Name);
    }

    private static int[] _Glyphs(string masked)
    {
        var glyphs = new List<int>();
        for (var i = 0; i < masked.Length; i += char.IsSurrogatePair(masked, i) ? 2 : 1)
        {
            int codepoint = char.ConvertToUtf32(masked, i);
            if (codepoint is not ('⁦' or '⁩'))
                glyphs.Add(codepoint);
        }

        return glyphs.ToArray();
    }
}
