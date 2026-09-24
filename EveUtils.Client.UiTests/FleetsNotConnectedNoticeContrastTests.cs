using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using EveUtils.Client.Theming;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-13: the not-connected notice line (WarnBrush text) measured as numbers in every theme, over each stop of the
/// window gradient and over the panel surface composited on it. WCAG 4.5:1 is the floor the project holds text to.
/// </summary>
public sealed class FleetsNotConnectedNoticeContrastTests
{
    private const double TextFloor = 4.5;

    [AvaloniaTheory]
    [InlineData(FactionTheme.Caldari)]
    [InlineData(FactionTheme.Amarr)]
    [InlineData(FactionTheme.Gallente)]
    [InlineData(FactionTheme.Minmatar)]
    public void NoticeLine_ReadsOnTheWindowAndOnThePanel_InEveryTheme(FactionTheme faction)
    {
        using var instance = TestClientInstance.Create();
        var theme = instance.Services.GetRequiredService<IThemeService>();
        try
        {
            theme.Apply(faction);
            Color text = _Resource("WarnBrush");
            Color panel = _Resource("BgPanelBrush");

            if (!Application.Current!.TryGetResource("WindowBackgroundBrush", null, out object? value)
                || value is not LinearGradientBrush gradient)
                throw new Xunit.Sdk.XunitException("WindowBackgroundBrush is not a linear gradient");

            List<string> measured = [];
            List<string> failures = [];
            foreach (var stop in gradient.GradientStops)
                foreach (var (surfaceName, surface) in new[]
                         {
                             ($"window {stop.Color}", stop.Color),
                             ($"panel over {stop.Color}", _Composite(panel, stop.Color))
                         })
                {
                    double contrast = _Contrast(text, surface);
                    measured.Add($"{surfaceName}: {contrast:F2}:1");
                    if (contrast < TextFloor)
                        failures.Add($"{surfaceName}: {contrast:F2}:1");
                }

            Assert.True(failures.Count == 0,
                $"[{faction}] under {TextFloor}:1 — {string.Join("; ", failures)} (all: {string.Join("; ", measured)})");
        }
        finally
        {
            theme.Apply(FactionTheme.Gallente);
        }
    }

    private static Color _Resource(string key) =>
        Application.Current!.TryGetResource(key, null, out object? value) && value is ISolidColorBrush brush
            ? brush.Color
            : throw new Xunit.Sdk.XunitException($"resource {key} is not a solid brush");

    private static Color _Composite(Color foreground, Color background)
    {
        double alpha = foreground.A / 255.0;
        return Color.FromRgb(
            (byte)Math.Round(foreground.R * alpha + background.R * (1 - alpha)),
            (byte)Math.Round(foreground.G * alpha + background.G * (1 - alpha)),
            (byte)Math.Round(foreground.B * alpha + background.B * (1 - alpha)));
    }

    private static double _RelativeLuminance(Color color)
    {
        static double Linear(byte channel)
        {
            double s = channel / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
    }

    private static double _Contrast(Color a, Color b)
    {
        double lighter = Math.Max(_RelativeLuminance(a), _RelativeLuminance(b));
        double darker = Math.Min(_RelativeLuminance(a), _RelativeLuminance(b));
        return (lighter + 0.05) / (darker + 0.05);
    }
}
