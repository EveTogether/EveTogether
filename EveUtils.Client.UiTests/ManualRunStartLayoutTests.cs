using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EveUtils.Client.Dialogs;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Client.Views;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Sde.Dtos;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using CqrsDispatcher = EveUtils.Shared.Cqrs.IDispatcher;

namespace EveUtils.Client.UiTests;

/// <summary>ET-163 nazorg: START RUN is a dialog, and a dialog is as tall as what is in it. What it looks like is
/// checked by rendering it and looking (<see cref="OverlayShots"/>) — the two layout faults this project has caught
/// that way were both invisible to every assertion in the suite.</summary>
public class ManualRunStartLayoutTests
{
    private static readonly SdeSite Site = new(4321, "Sansha's Nest", null, null, null, null, null, null, false, []);

    private static ManualRunStartWindow _Dialog(TestClientInstance instance) =>
        new(new ManualRunStartViewModel(instance.Services.GetRequiredService<CqrsDispatcher>(),
            instance.Services.GetRequiredService<ISdeAccessor>(), new RecordingDialogService(),
            kind => new ActivityWindowViewModel(kind, instance.Services), []));

    // The complaint this replaces: a fixed Height="560" on a form that measured about 420, so a third of the
    // window was content and the rest was black. A dialog takes its height from its content or it is not a dialog.
    [AvaloniaFact]
    public void TheDialog_TakesItsHeightFromItsContent()
    {
        using var instance = TestClientInstance.Create(services =>
            services.AddSingleton<ISdeAccessor>(new FakeSdeAccessor().AddSite(Site)));
        var window = _Dialog(instance);

        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        Assert.Equal(SizeToContent.Height, window.SizeToContent);
        Assert.False(window.CanResize, "a fixed dialog, not a module window");

        // The counterproof the mode alone does not give: an empty band under the buttons is exactly the fault, so
        // the window may not be meaningfully taller than the form it asked for (DesiredSize, margin included) plus
        // ChromedWindow's own 40px titlebar and its 1px border either side.
        double form = ((Control)window.Content!).DesiredSize.Height + 40 + 2;
        Assert.True(window.Bounds.Height <= form + 2,
            $"window is {window.Bounds.Height:F0}px for a {form:F0}px form — empty space below");

        Assert.NotNull(window.CaptureRenderedFrame());
        OverlayShots.Capture(window, "eveutils-manual-run-start");

        // The dialog at its tallest — a search with results open, a site picked and the two backdate fields out.
        // Nothing may fall outside the width it is fixed to, and it still has to grow rather than scroll.
        var vm = (ManualRunStartViewModel)window.DataContext!;
        vm.SiteQuery = "Sansha";
        vm.SelectedOption = new SdeSitePickerOption(Site, Site.Name);
        vm.IsBackdated = true;
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        foreach (Control control in ((Control)window.Content!).GetVisualDescendants().OfType<Control>()
                     .Where(c => c is Button or TextBox or ComboBox or ListBox or ToggleButton or DatePicker or TimePicker && c.IsVisible))
        {
            double right = (control.TranslatePoint(default, window) ?? default).X + control.Bounds.Width;
            Assert.True(right <= window.Bounds.Width + 0.5,
                $"{control.GetType().Name} runs off the dialog: right edge {right:F1} > {window.Bounds.Width:F0}");
        }

        OverlayShots.Capture(window, "eveutils-manual-run-start-filled");
        window.Close();
    }

    // ET-79: everything a pilot reads is English. "ACHTERAF INVOEREN" shipped on this very dialog and no test saw
    // it, because no test was looking — this one reads the XAML the way a pilot reads the screen.
    [Fact]
    public void NoScreenShowsDutchText()
    {
        var dutch = new Regex(
            @"\b(achteraf|invoeren|nieuwe|karakter|soort|opslaan|sluiten|annuleer|annuleren|verwijderen|toevoegen|" +
            @"zoeken|instellingen|starten|stoppen|geen|niet|deze|wordt|loopt|klembord|handmatig|missie|overig|" +
            @"kiezen|bewerken|opnieuw|volgende|vorige|bevestigen|melding|gestart|gestopt|onbekend|lopende|vandaag|" +
            @"gisteren|terug|tijd)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // Only what is shown: a Dutch word in a comment or an x:Name is nobody's screen.
        var shown = new Regex(
            @"(?:Text|Content|Header|Title|PlaceholderText|Watermark|ToolTip\.Tip)\s*=\s*""([^""{}]+)""",
            RegexOptions.CultureInvariant);

        var offences = Directory
            .EnumerateFiles(_ViewsDirectory(), "*.axaml", SearchOption.AllDirectories)
            .SelectMany(file => shown.Matches(File.ReadAllText(file))
                .Where(match => dutch.IsMatch(match.Groups[1].Value))
                .Select(match => $"{Path.GetFileName(file)}: \"{match.Groups[1].Value}\""))
            .ToList();

        Assert.True(offences.Count == 0, "Dutch text on screen (ET-79 — the app is English):\n" + string.Join("\n", offences));
    }

    private static string _ViewsDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EVE-Together.slnx")))
            directory = directory.Parent;

        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "EveUtils.Client", "Views");
    }
}
