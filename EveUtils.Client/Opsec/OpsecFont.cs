using Avalonia;
using Avalonia.Media;

namespace EveUtils.Client.Opsec;

/// <summary>
/// The face masked locations are drawn in (ET-417): Noto Sans Old Turkic, SIL Open Font License 1.1 (licence shipped
/// beside it in Assets/Fonts). Registered as a font fallback rather than set on any control, so a mask inside any
/// text block, in whatever face that block uses, finds its glyphs here — on every platform, whatever fonts the system has.
/// </summary>
public static class OpsecFont
{
    public static readonly FontFamily Family = new("avares://EveUtils.Client/Assets/Fonts#Noto Sans Old Turkic");

    public static AppBuilder WithOpsecFont(this AppBuilder builder) =>
        builder.With(new FontManagerOptions { FontFallbacks = [new FontFallback { FontFamily = Family }] });
}
