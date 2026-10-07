using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using EveUtils.Client.Theming;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-475: the DROPPED (green) and DESTROYED (red) amounts of the killmail item table, measured as numbers in every
/// theme over each stop of the window gradient, over the panel surface composited on it, and over the row hover fill
/// on top of that. WCAG 4.5:1 is the floor the project holds text to.
/// </summary>
public sealed class KillmailItemColumnsContrastTests
{
    private const double TextFloor = 4.5;

    [AvaloniaTheory]
    [InlineData(FactionTheme.Caldari)]
    [InlineData(FactionTheme.Amarr)]
    [InlineData(FactionTheme.Gallente)]
    [InlineData(FactionTheme.Minmatar)]
    public void DroppedAndDestroyedAmounts_Read_InEveryTheme(FactionTheme faction)
    {
        using var instance = TestClientInstance.Create();
        var theme = instance.Services.GetRequiredService<IThemeService>();
        try
        {
            theme.Apply(faction);
            Color panel = _Resource("BgPanelBrush");
            Color hover = _Resource("BgRowHoverBrush");

            if (!Application.Current!.TryGetResource("WindowBackgroundBrush", null, out object? value)
                || value is not LinearGradientBrush gradient)
                throw new Xunit.Sdk.XunitException("WindowBackgroundBrush is not a linear gradient");

            List<string> failures = [];
            foreach (string inkName in new[] { "GreenBrush", "RedBrush" })
            {
                Color ink = _Resource(inkName);
                foreach (var stop in gradient.GradientStops)
                {
                    Color onPanel = _Composite(panel, stop.Color);
                    foreach (var (surfaceName, surface) in new[]
                             {
                                 ("window", stop.Color), ("panel", onPanel), ("hover", _Composite(hover, onPanel))
                             })
                    {
                        double contrast = _Contrast(ink, surface);
                        if (contrast < TextFloor)
                            failures.Add($"{inkName} on {surfaceName} over {stop.Color}: {contrast:F2}:1");
                    }
                }
            }

            Assert.True(failures.Count == 0, $"[{faction}] under {TextFloor}:1 — {string.Join("; ", failures)}");
        }
        finally
        {
            theme.Apply(FactionTheme.Gallente);
        }
    }

    [AvaloniaTheory]
    [InlineData(FactionTheme.Caldari)]
    [InlineData(FactionTheme.Amarr)]
    [InlineData(FactionTheme.Gallente)]
    [InlineData(FactionTheme.Minmatar)]
    public void GroupHeaderBand_Reads_InEveryTheme(FactionTheme faction)
    {
        using var instance = TestClientInstance.Create();
        var theme = instance.Services.GetRequiredService<IThemeService>();
        try
        {
            theme.Apply(faction);
            Color panel = _Resource("BgPanelBrush");
            Color band = _Resource("BgRowHoverBrush");

            if (!Application.Current!.TryGetResource("WindowBackgroundBrush", null, out object? value)
                || value is not LinearGradientBrush gradient)
                throw new Xunit.Sdk.XunitException("WindowBackgroundBrush is not a linear gradient");

            List<string> failures = [];
            foreach (string inkName in new[] { "TextBrightBrush", "TextBrush", "GreenBrush", "RedBrush" })
            {
                Color ink = _Resource(inkName);
                foreach (var stop in gradient.GradientStops)
                {
                    Color surface = _Composite(band, _Composite(panel, stop.Color));
                    double contrast = _Contrast(ink, surface);
                    if (contrast < TextFloor)
                        failures.Add($"{inkName} on header band over {stop.Color}: {contrast:F2}:1");
                }
            }

            Assert.True(failures.Count == 0, $"[{faction}] under {TextFloor}:1 — {string.Join("; ", failures)}");
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
