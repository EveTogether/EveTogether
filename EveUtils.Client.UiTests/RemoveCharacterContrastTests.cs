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
/// ET-345: the Remove character button and its inline confirmation, measured the way Jithran asked — numbers, not a
/// render. The colours are read back from the styled controls in each state, so a style change is measured too, and
/// composited over every stop of the window gradient and over the confirmation panel on top of it. WCAG 4.5:1 is the
/// floor the project holds text to.
/// </summary>
public sealed class RemoveCharacterContrastTests
{
    private const double TextFloor = 4.5;

    [AvaloniaTheory]
    [InlineData(FactionTheme.Caldari)]
    [InlineData(FactionTheme.Amarr)]
    [InlineData(FactionTheme.Gallente)]
    [InlineData(FactionTheme.Minmatar)]
    public void DangerButton_ReadsInEveryState_OnTheWindowAndOnTheConfirmationPanel(FactionTheme faction)
    {
        using var instance = TestClientInstance.Create();
        var theme = instance.Services.GetRequiredService<IThemeService>();
        try
        {
            theme.Apply(faction);
            var button = new Button { Content = "Remove character…", Classes = { "danger" } };
            var window = new Window { Content = button };
            window.Show();
            var presenter = _Presenter(button);

            List<string> failures = [];
            foreach (var (state, enter, leave) in _ButtonStates())
            {
                enter(button);
                Dispatcher.UIThread.RunJobs();
                foreach (var (surfaceName, surface) in _Surfaces())
                {
                    double contrast = _Contrast(_Solid(presenter.Foreground),
                        _Composite(_Solid(presenter.Background), surface));
                    if (contrast < TextFloor)
                        failures.Add($"{state} on {surfaceName}: {contrast:F2}:1");
                }
                leave(button);
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
    public void ConfirmationText_ReadsOnThePanel_AndTheBlockedReasonOnTheWindow(FactionTheme faction)
    {
        using var instance = TestClientInstance.Create();
        var theme = instance.Services.GetRequiredService<IThemeService>();
        try
        {
            theme.Apply(faction);
            var checkBox = new CheckBox { Content = "Also delete this character's runs and fittings" };
            var window = new Window { Content = checkBox };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Color checkBoxText = _Solid(_Presenter(checkBox).Foreground);
            window.Close();

            List<string> failures = [];
            var panels = _Surfaces().Where(surface => surface.Name.StartsWith("panel", StringComparison.Ordinal)).ToList();
            var windowStops = _Surfaces().Where(surface => surface.Name.StartsWith("window", StringComparison.Ordinal)).ToList();
            foreach (var (textName, text, surfaces) in new (string, Color, List<(string Name, Color Color)>)[]
                     {
                         ("question (TextBrightBrush)", _Resource("TextBrightBrush"), panels),
                         ("explanation (TextBrush)", _Resource("TextBrush"), panels),
                         ("open-run note (WarnBrush)", _Resource("WarnBrush"), panels),
                         ("checkbox", checkBoxText, panels),
                         ("blocked reason (TextBrush)", _Resource("TextBrush"), windowStops)
                     })
                foreach (var (surfaceName, surface) in surfaces)
                {
                    double contrast = _Contrast(text, surface);
                    if (contrast < TextFloor)
                        failures.Add($"{textName} on {surfaceName}: {contrast:F2}:1");
                }

            Assert.True(failures.Count == 0, $"[{faction}] under {TextFloor}:1 — {string.Join("; ", failures)}");
        }
        finally
        {
            theme.Apply(FactionTheme.Gallente);
        }
    }

    private static IEnumerable<(string State, Action<Button> Enter, Action<Button> Leave)> _ButtonStates()
    {
        yield return ("normal", _ => { }, _ => { });
        yield return ("hover", b => _PseudoClass(b, ":pointerover", true), b => _PseudoClass(b, ":pointerover", false));
        yield return ("pressed", b => _PseudoClass(b, ":pressed", true), b => _PseudoClass(b, ":pressed", false));
        yield return ("disabled", b => b.IsEnabled = false, b => b.IsEnabled = true);
    }

    private static void _PseudoClass(Button button, string name, bool on) =>
        ((IPseudoClasses)button.Classes).Set(name, on);

    /// <summary>Every stop of the window gradient, and the confirmation panel (BgPanelBrush) over each of them.</summary>
    private static List<(string Name, Color Color)> _Surfaces()
    {
        if (!Application.Current!.TryGetResource("WindowBackgroundBrush", null, out object? value)
            || value is not LinearGradientBrush gradient)
            throw new Xunit.Sdk.XunitException("WindowBackgroundBrush is not a linear gradient");

        Color panel = _Resource("BgPanelBrush");
        List<(string, Color)> surfaces = [];
        foreach (var stop in gradient.GradientStops)
        {
            surfaces.Add(($"window {stop.Color}", stop.Color));
            surfaces.Add(($"panel over {stop.Color}", _Composite(panel, stop.Color)));
        }
        return surfaces;
    }

    private static ContentPresenter _Presenter(Control control) =>
        control.GetVisualDescendants().OfType<ContentPresenter>().First(presenter => presenter.Name == "PART_ContentPresenter");

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
