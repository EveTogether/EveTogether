using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using EveUtils.Client.Controls.Map;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-392 acceptance: every text colour the map module uses reaches WCAG 4.5:1 on every background it sits on, in all
/// four faction themes — measured from the colours themselves, not judged off a render. Backgrounds are composited the
/// way they stack: the window gradient's stops alone and under the panel fill, the header gradient on top of that,
/// the map canvas, and the HUD card over the canvas.
/// </summary>
public sealed class MapContrastTests
{
    private const double Minimum = 4.5;

    [AvaloniaTheory]
    [InlineData("Amarr")]
    [InlineData("Caldari")]
    [InlineData("Gallente")]
    [InlineData("Minmatar")]
    public void EveryMapText_OnEveryBackgroundItSitsOn_ReachesFourAndAHalf(string faction)
    {
        var theme = (ResourceDictionary)AvaloniaXamlLoader.Load(new Uri($"avares://EveUtils.Client/Themes/Factions/{faction}.axaml"));
        Color accentBright = ((ISolidColorBrush)theme["AccentBrightBrush"]!).Color;
        Color headerTop = ((LinearGradientBrush)theme["HeaderGradientBrush"]!).GradientStops[0].Color;
        Color[] windowStops = [.. ((LinearGradientBrush)theme["WindowBackgroundBrush"]!).GradientStops.Select(stop => stop.Color)];
        Color panelFill = _Neutral("BgPanelBrush");

        Color[] panels = [.. windowStops, .. windowStops.Select(stop => _Over(panelFill, stop))];
        Color[] headers = [.. panels.Select(panel => _Over(headerTop, panel))];
        Color canvas = MapPalette.Background;
        Color hud = _Over(MapPalette.HudBackground, canvas);

        Color[] panelTexts =
        [
            _Neutral("TextBrush"), _Neutral("TextDimBrush"), _Neutral("TextDimSmallBrush"), _Neutral("TextBrightBrush"), accentBright,
            .. MapPalette.Security, .. MapPalette.Regions
        ];
        Color[] canvasTexts = [MapPalette.Text, MapPalette.Muted, accentBright, _Neutral("TextDimSmallBrush"), .. MapPalette.Security, .. MapPalette.Regions];
        Color[] hudTexts = [_Neutral("TextBrush"), _Neutral("TextBrightBrush"), accentBright, .. MapPalette.Security, .. MapPalette.Regions];
        Color[] headerTexts = [_Neutral("TextBrush"), _Neutral("TextBrightBrush")];

        List<(string Where, Color Ink, Color Ground, double Ratio)> pairs =
        [
            .. _Pairs("side panel", panelTexts, panels),
            .. _Pairs("header", headerTexts, headers),
            .. _Pairs("map canvas", canvasTexts, [canvas]),
            .. _Pairs("HUD", hudTexts, [hud])
        ];

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{faction}: {pairs.Count} pairs, lowest {pairs.Min(pair => pair.Ratio):0.00}:1 ({pairs.MinBy(pair => pair.Ratio).Where})");
        Assert.All(pairs, pair => Assert.True(pair.Ratio >= Minimum,
            $"{pair.Where}: {pair.Ink} on {pair.Ground} is {pair.Ratio:0.00}:1"));
    }

    private static IEnumerable<(string, Color, Color, double)> _Pairs(string where, Color[] inks, Color[] grounds) =>
        inks.SelectMany(ink => grounds.Select(ground => (where, ink, ground, _Ratio(ink, ground))));

    private static Color _Neutral(string key) =>
        Application.Current is { } app && app.TryGetResource(key, app.ActualThemeVariant, out object? value) && value is ISolidColorBrush brush
            ? brush.Color
            : throw new InvalidOperationException($"{key} is not a solid brush in the app resources");

    private static Color _Over(Color top, Color ground)
    {
        double alpha = top.A / 255.0;
        return Color.FromRgb(
            (byte)Math.Round(top.R * alpha + ground.R * (1 - alpha)),
            (byte)Math.Round(top.G * alpha + ground.G * (1 - alpha)),
            (byte)Math.Round(top.B * alpha + ground.B * (1 - alpha)));
    }

    private static double _Ratio(Color a, Color b)
    {
        double la = _Luminance(a), lb = _Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double _Luminance(Color colour) =>
        0.2126 * _Channel(colour.R) + 0.7152 * _Channel(colour.G) + 0.0722 * _Channel(colour.B);

    private static double _Channel(byte value)
    {
        double channel = value / 255.0;
        return channel <= 0.03928 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);
    }
}
