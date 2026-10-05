using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EveUtils.Client.Theming;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-388: the manual start's new elements — the earlier-run suggestion rows, the site-group choice and its label,
/// the "Selected:" line — measured as numbers in every faction theme, over every stop of the window gradient. The
/// list colours are read back from the styled items in each state, so a style change is measured too. WCAG 4.5:1 is
/// the floor the project holds text to.
/// </summary>
public sealed class ManualRunStartContrastTests
{
    private const double TextFloor = 4.5;

    [AvaloniaTheory]
    [InlineData(FactionTheme.Caldari)]
    [InlineData(FactionTheme.Amarr)]
    [InlineData(FactionTheme.Gallente)]
    [InlineData(FactionTheme.Minmatar)]
    public void ChoiceRows_ReadInEveryState_OverTheWindow(FactionTheme faction)
    {
        using var instance = TestClientInstance.Create();
        var theme = instance.Services.GetRequiredService<IThemeService>();
        try
        {
            theme.Apply(faction);
            var list = new ListBox { ItemsSource = new[] { "Relic Site", "Data Site" }, SelectedIndex = 0 };
            var window = new Window { Content = list };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            List<ListBoxItem> items = [.. list.GetVisualDescendants().OfType<ListBoxItem>()];
            ListBoxItem selected = items[0];
            ListBoxItem plain = items[1];

            List<string> failures = [];
            var surfaces = _WindowStops();
            foreach (var (state, item, pseudoClass) in new (string, ListBoxItem, string?)[]
                     {
                         ("normal", plain, null),
                         ("hover", plain, ":pointerover"),
                         ("selected", selected, null),
                         ("selected + hover", selected, ":pointerover")
                     })
            {
                if (pseudoClass is not null)
                    ((IPseudoClasses)item.Classes).Set(pseudoClass, true);
                Dispatcher.UIThread.RunJobs();
                ContentPresenter presenter = item.GetVisualDescendants().OfType<ContentPresenter>()
                    .First(candidate => candidate.Name == "PART_ContentPresenter");
                foreach (var (surfaceName, surface) in surfaces)
                {
                    double contrast = _Contrast(_Solid(presenter.Foreground), _Composite(_Solid(presenter.Background), surface));
                    if (contrast < TextFloor)
                        failures.Add($"{state} on {surfaceName}: {contrast:F2}:1");
                }
                if (pseudoClass is not null)
                    ((IPseudoClasses)item.Classes).Set(pseudoClass, false);
            }
            window.Close();

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
    public void LabelAndSelectedLine_ReadOverTheWindow(FactionTheme faction)
    {
        using var instance = TestClientInstance.Create();
        var theme = instance.Services.GetRequiredService<IThemeService>();
        try
        {
            theme.Apply(faction);
            var label = new TextBlock { Classes = { "label" }, Text = "KIND OF SITE" };
            var window = new Window { Content = label };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Color labelText = _Solid(label.Foreground);
            window.Close();

            List<string> failures = [];
            foreach (var (name, text) in new (string, Color)[]
                     {
                         ("label", labelText),
                         ("selected line (TextAccentBrush)", _Resource("TextAccentBrush"))
                     })
                foreach (var (surfaceName, surface) in _WindowStops())
                {
                    double contrast = _Contrast(text, surface);
                    if (contrast < TextFloor)
                        failures.Add($"{name} on {surfaceName}: {contrast:F2}:1");
                }

            Assert.True(failures.Count == 0, $"[{faction}] under {TextFloor}:1 — {string.Join("; ", failures)}");
        }
        finally
        {
            theme.Apply(FactionTheme.Gallente);
        }
    }

    private static List<(string Name, Color Color)> _WindowStops()
    {
        if (!Application.Current!.TryGetResource("WindowBackgroundBrush", null, out object? value)
            || value is not LinearGradientBrush gradient)
            throw new Xunit.Sdk.XunitException("WindowBackgroundBrush is not a linear gradient");

        return [.. gradient.GradientStops.Select(stop => ($"window {stop.Color}", stop.Color))];
    }

    private static Color _Resource(string key) =>
        Application.Current!.TryGetResource(key, null, out object? value) && value is ISolidColorBrush brush
            ? brush.Color
            : throw new Xunit.Sdk.XunitException($"resource {key} is not a solid brush");

    private static Color _Solid(IBrush? brush) => brush switch
    {
        null => Colors.Transparent,
        ISolidColorBrush solid => Color.FromArgb((byte)Math.Round(solid.Color.A * solid.Opacity), solid.Color.R, solid.Color.G, solid.Color.B),
        _ => throw new Xunit.Sdk.XunitException($"expected a solid brush, got {brush.GetType()}")
    };

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
