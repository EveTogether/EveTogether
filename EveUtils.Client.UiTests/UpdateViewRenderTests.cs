using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using EveUtils.Client.Theming;
using EveUtils.Client.Updates;
using EveUtils.Client.ViewModels;
using EveUtils.Client.Views;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// Renders the three update surfaces headlessly, so the suite actually loads the XAML behind them: a typo in the
/// offer window, the restart banner or the About block would otherwise only surface when a user opened it.
/// </summary>
public class UpdateViewRenderTests
{
    private static readonly AppRelease Release = new(
        "0.3.0",
        "- Fleet compositions — save a doctrine and check a live fleet against it.\n" +
        "- Faction themes — Amarr, Caldari and Minmatar palettes next to Gallente.\n" +
        "- Fixed: gamelog watcher missed rotated files on Linux.",
        "https://github.com/EveTogether/EveTogether/releases/tag/v0.3.0",
        81_788_928);

    private sealed class Probe(UpdateSupport support) : IUpdateSupportProbe
    {
        public UpdateSupport Detect() => support;
    }

    [AvaloniaFact]
    public void UpdateOffer_Renders()
    {
        using var instance = TestClientInstance.Create();
        instance.Services.GetRequiredService<IThemeService>().Apply(FactionTheme.Gallente);

        Render(new UpdateAvailableWindow("v0.2.0-beta", Release));
    }

    // A feed that reports no size must not render "0 MB" — the whole column steps aside instead.
    [AvaloniaFact]
    public void UpdateOffer_WithoutASizeOrNotes_Renders()
    {
        using var instance = TestClientInstance.Create();

        Render(new UpdateAvailableWindow("v0.2.0-beta", Release with { SizeBytes = 0, Notes = "" }));
    }

    [AvaloniaFact]
    public void RestartBanner_Renders()
    {
        using var instance = TestClientInstance.Create();
        instance.Services.GetRequiredService<IThemeService>().Apply(FactionTheme.Gallente);

        var viewModel = new MainWindowViewModel(instance.Services)
        {
            IsUpdateReady = true,
            UpdateReadyMessage = "v0.3.0 is ready. Restart to finish updating.",
            ActivityStatus = "Update v0.3.0 downloaded — restart to finish updating.",
        };

        Render(new MainWindow { DataContext = viewModel, Width = 1100, Height = 640 });
    }

    [AvaloniaTheory]
    [InlineData(UpdateSupport.Supported)]
    [InlineData(UpdateSupport.NotInstalled)]
    public void About_RendersItsUpdatesBlock(UpdateSupport support)
    {
        using var instance = TestClientInstance.Create();
        instance.Services.GetRequiredService<IThemeService>().Apply(FactionTheme.Gallente);

        Render(new AboutWindow(new AboutViewModel(null, null, new FakeUpdateService(), new Probe(support))));
    }

    // Rendering is the assertion: a window whose XAML fails to load throws on construction, and one that lays out to
    // nothing hands back no frame.
    private static void Render(Window window)
    {
        window.Show();
        for (var i = 0; i < 8; i++)
            Dispatcher.UIThread.RunJobs();

        Assert.NotNull(window.CaptureRenderedFrame());
        window.Close();
    }
}
