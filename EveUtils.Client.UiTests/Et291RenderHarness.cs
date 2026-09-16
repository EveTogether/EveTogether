// Throwaway render harness for ET-291 — the activity pane beside the list at 1303 and the drawer over it at 758, in
// all four faction themes, on a SQLite backup copy of the real client store. Not a test that guards anything; removed
// before the PR is merged.
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Theming;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Client.Views;
using EveUtils.Shared.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using ICqrsDispatcher = EveUtils.Shared.Cqrs.IDispatcher;

namespace EveUtils.Client.UiTests;

public class Et291RenderHarness
{
    private static readonly string OutDir = Environment.GetEnvironmentVariable("ET291_SHOTS")
        ?? Path.Combine(Path.GetTempPath(), "et291", "renders");

    private static readonly FactionTheme[] Themes =
        [FactionTheme.Caldari, FactionTheme.Amarr, FactionTheme.Gallente, FactionTheme.Minmatar];

    private sealed class FakeDisplay : IModuleHostDisplay
    {
        public bool IsFloating { get; set; }
        public ObservableCollection<HostTab> HostTabs { get; } = new();
        public HostTab? SelectedHostTab { get; set; }
    }

    /// <summary>A copy of the live store taken through SQLite's own backup API rather than File.Copy: his client is
    /// running and writing its WAL while this reads.</summary>
    private static string _BackupLiveStore()
    {
        string source = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EveUtils", "client.db");
        string instance = "et291-" + Guid.NewGuid().ToString("N")[..12];
        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EveUtils", instance);
        Directory.CreateDirectory(directory);

        using var live = new SqliteConnection($"Data Source={source};Mode=ReadOnly;Cache=Shared");
        live.Open();
        using var copy = new SqliteConnection($"Data Source={Path.Combine(directory, "client.db")}");
        copy.Open();
        live.BackupDatabase(copy);

        // The static data is anchored to the instance's own data directory, so the scratch instance would have none
        // and every row would read its system as a dash. Copied rather than linked: the instance directory is
        // deleted whole on dispose, and the real store is 60 MB nobody wants to download again.
        string sde = Path.Combine(Path.GetDirectoryName(source)!, "sde", "sde.sqlite");
        if (File.Exists(sde))
        {
            Directory.CreateDirectory(Path.Combine(directory, "sde"));
            File.Copy(sde, Path.Combine(directory, "sde", "sde.sqlite"), overwrite: true);
        }

        return instance;
    }

    private static void Settle(int times = 12)
    {
        for (int index = 0; index < times; index++)
            Dispatcher.UIThread.RunJobs();
    }

    private static void Shot(Window window, string name)
    {
        Settle();
        window.UpdateLayout();
        Settle(4);
        Directory.CreateDirectory(OutDir);
        window.CaptureRenderedFrame()!.Save(Path.Combine(OutDir, name + ".png"), new PngBitmapEncoderOptions());
    }

    private static void Log(StringBuilder log, string title, Control root)
    {
        log.AppendLine($"── {title} ──  root w={root.Bounds.Width:0.##} h={root.Bounds.Height:0.##}");
        foreach (Control control in root.GetVisualDescendants().OfType<Control>())
        {
            if (control.Name is not { Length: > 0 } name)
                continue;
            if (name is not ("ActivityList" or "PaneHost" or "Drawer" or "Scrim" or "PaneHead" or "PaneActions"
                or "PaneCrew" or "PaneIskParts" or "PaneScroll" or "PaneBody" or "PaneEmpty" or "PaneOpenDetail"
                or "PanePublish" or "PaneLoot" or "PaneEnemies" or "DrawerClose"))
                continue;

            Point? at = control.TranslatePoint(new Point(0, 0), root);
            log.AppendLine($"   {name,-16} x={at?.X,8:0.##} w={control.Bounds.Width,8:0.##} h={control.Bounds.Height,8:0.##} visible={control.IsVisible}");
        }
    }

    [AvaloniaFact]
    public async Task Render_All()
    {
        string instanceName = _BackupLiveStore();
        using var instance = TestClientInstance.Create(services =>
            services.AddSingleton<IDialogService>(new RecordingDialogService()), instanceName);

        var dispatcher = instance.Services.GetRequiredService<ICqrsDispatcher>();
        IReadOnlyList<Character> characters = await instance.Services
            .GetRequiredService<ICharacterRegistry>().GetAllAsync();

        var log = new StringBuilder();
        log.AppendLine($"instance {instanceName} · {characters.Count} characters");

        foreach (FactionTheme theme in Themes)
        {
            instance.Services.GetRequiredService<IThemeService>().Apply(theme);

            foreach (double width in new[] { 1303d, 758d })
            {
                var viewModel = new RunsOverviewViewModel(dispatcher, new RecordingDialogService(), instance.Services,
                    characters, runClock: false, paneReadDelay: TimeSpan.Zero);
                // The mockup's own evening. ViewedMonthLocal starts on the current month, which is this one.
                await viewModel.LoadAsync();

                var window = new RunsWindow(viewModel) { Width = width, Height = 1400 };
                var display = new FakeDisplay();
                var host = new ModuleHostService();
                host.SetOwner(new Window());
                host.SetHost(display);
                host.Open(window, "RUNS", "runs", "runs");
                var content = (Control)display.HostTabs.Single().Content!;
                var root = new Window { Width = width, Height = 1400, Content = content };
                root.Show();
                Settle();
                root.UpdateLayout();

                // No transitions in a render: the drawer's 0.22 s slide is clock-driven and a captured frame would
                // otherwise catch it halfway.
                if (content.FindControl<Border>("Drawer") is { } drawer)
                    drawer.Transitions = null;
                if (content.FindControl<Border>("Scrim") is { } scrim)
                    scrim.Transitions = null;

                string tag = $"{theme.ToString().ToLowerInvariant()}-{width:0}";
                Shot(root, $"01-{tag}-no-selection");
                Log(log, $"{tag} · nothing selected", content);

                // The 13 September homefront at 21:52 the mockup is drawn from; the newest day's first row where
                // this machine's store does not hold it.
                RunsDayViewModel? day = viewModel.Tabs[0].Days.FirstOrDefault(candidate => candidate.Day.Day == 13)
                    ?? viewModel.Tabs[0].Days.FirstOrDefault();
                if (day is null)
                {
                    log.AppendLine($"{tag}: no days in the viewed month — nothing to select");
                    continue;
                }

                day.IsExpanded = true;
                Settle();
                ActivityOverviewRowViewModel row = day.Rows.FirstOrDefault(candidate => candidate.TimeText == "21:52")
                    ?? day.Rows[0];
                viewModel.Select(row);
                // The pane's crew and loot come from a query on a background thread; wait for it rather than
                // capturing the head alone.
                await ActivityWindowHarness.WaitUntil(() => viewModel.Pane.Crew.Count > 0);
                Settle();
                root.UpdateLayout();
                Settle();

                Shot(root, $"02-{tag}-selected");
                Log(log, $"{tag} · {row.TimeText} {row.SiteText} selected · drawer={viewModel.IsDrawerOpen}", content);

                // Folded with the selection standing: the pane or the drawer keeps showing that run.
                day.IsExpanded = false;
                Settle();
                root.UpdateLayout();
                Shot(root, $"03-{tag}-day-folded");
                log.AppendLine($"{tag} · folded: selection={viewModel.SelectedRow?.TimeText ?? "(none)"} "
                    + $"listSelection={(viewModel.ListSelection is null ? "null" : "row")} drawer={viewModel.IsDrawerOpen} "
                    + $"pane={viewModel.Pane.SiteText}");
                day.IsExpanded = true;
                Settle();

                root.Content = null;
                root.Close();
                window.Close();
            }
        }

        Directory.CreateDirectory(OutDir);
        await File.WriteAllTextAsync(Path.Combine(OutDir, "log.txt"), log.ToString());
    }
}
