using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Fleet;
using EveUtils.Client.Theming;
using EveUtils.Client.Transport;
using EveUtils.Client.ViewModels;
using EveUtils.Client.ViewModels.Fleets;
using EveUtils.Client.Views;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Enums;
using EveUtils.Shared.Transport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>ET-407: the fleet list's section headings, the direct METRICS button and the sentence-case "⋯" menu.</summary>
public class FleetOverviewListTests
{
    private const string Server = "krahwinkel-it.nl:7443";
    private const int Me = 95400101;

    private static FleetInfo Fleet(long id, string name, FleetActivation activation) =>
        new(id, name, null, FleetVisibility.InviteOnly, FleetState.Active, Me, null, null, DateTimeOffset.UtcNow.AddDays(-3),
            activation, ActivatedAt: activation == FleetActivation.Forming ? null : DateTimeOffset.UtcNow.AddHours(-1));

    private static async Task<(TestClientInstance Instance, FleetsViewModel Vm)> BuildAsync()
    {
        var transport = new RecordingFleetTransportClient();
        transport.MyFleetsByServer[Server] =
        [
            Fleet(41, "Running", FleetActivation.Active),
            Fleet(42, "Waiting", FleetActivation.Forming),
            Fleet(43, "Over", FleetActivation.Concluded),
        ];
        foreach (var id in new long[] { 41, 42, 43 })
            transport.MembersByFleet[id] =
                [new FleetMemberInfo(id, Me, -1, 0, FleetRole.FleetCommander, false, null, null, default, DateTimeOffset.UtcNow)];
        transport.OpenFleetsByServer[Server] = [];

        var instance = TestClientInstance.Create(services =>
        {
            services.AddSingleton<IFleetTransportClient>(transport);
            services.AddSingleton<IDialogService>(new RecordingDialogService());
        });
        instance.Services.GetRequiredService<IThemeService>().Apply(FactionTheme.Gallente);
        await instance.Services.GetRequiredService<ICharacterRegistry>().AddOrUpdateAsync(new Character("Jithran", Me));
        await instance.Services.GetRequiredService<IClientSessionStore>()
            .SaveAsync(Server, new ClientSessionTokens("t", "r", "Jithran", Me));

        var vm = new FleetsViewModel(instance.Services, runClock: false);
        for (var i = 0; i < 300 && vm.ServerGroups.Sum(g => g.Fleets.Count) < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }

        Dispatcher.UIThread.RunJobs();
        return (instance, vm);
    }

    private static void Settle(Window window)
    {
        for (var i = 0; i < 12; i++)
            Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        for (var i = 0; i < 4; i++)
            Dispatcher.UIThread.RunJobs();
    }

    private static List<Button> ActionButtons(Window window, string fleet)
    {
        foreach (var cell in window.GetVisualDescendants().OfType<Border>()
                     .Where(b => b.Classes.Contains("cell") && b.Classes.Contains("acts") && b.IsVisible))
        {
            string? name = cell.FindAncestorOfType<Grid>()?.GetVisualDescendants().OfType<TextBlock>()
                .FirstOrDefault(t => t.Classes.Contains("nm"))?.Text;
            if (name == fleet)
                return cell.GetVisualDescendants().OfType<Button>().Where(b => b.IsVisible).ToList();
        }

        throw new InvalidOperationException($"No action cell for '{fleet}'");
    }

    [AvaloniaTheory]
    [InlineData(758)]
    [InlineData(1578)]
    public async Task ActiveRow_HasADirectMetricsButton_OnOneLine_AtEveryWidth(double width)
    {
        var (instance, vm) = await BuildAsync();
        using (instance)
        {
            var window = new FleetsWindow(vm) { Width = width, Height = 900 };
            window.Show();
            Settle(window);

            var buttons = ActionButtons(window, "Running");
            var metrics = Assert.Single(buttons, b => b.Content as string == "METRICS");
            Assert.Same(vm.MetricsRowCommand, metrics.Command);
            Assert.Equal(vm.ServerGroups.SelectMany(g => g.Fleets).Single(f => f.Name == "Running"), metrics.CommandParameter);

            int lines = buttons.Select(b => Math.Round(((Visual)b).TranslatePoint(new Point(0, 0), window)?.Y ?? 0)).Distinct().Count();
            Assert.Equal(1, lines);

            Assert.DoesNotContain(ActionButtons(window, "Waiting"), b => b.Content as string == "METRICS");

            window.Close();
            vm.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task OverflowItems_AreSentenceCase()
    {
        var (instance, vm) = await BuildAsync();
        using (instance)
        {
            var labels = vm.ServerGroups.SelectMany(g => g.Fleets).SelectMany(f => f.OverflowItems).Select(i => i.Label).ToList();

            Assert.Contains("Metrics", labels);
            Assert.Contains("Disband", labels);
            Assert.All(labels, label =>
            {
                Assert.True(char.IsUpper(label[0]), label);
                Assert.DoesNotMatch("[A-Z]{2,}", label);
            });
        }
    }

    [AvaloniaFact]
    public async Task SectionHeadings_AreLargerAndSeparatedByVerticalSpace()
    {
        var (instance, vm) = await BuildAsync();
        using (instance)
        {
            vm.ToggleFinishedCommand.Execute(null);
            var window = new FleetsWindow(vm) { Width = 1578, Height = 1100 };
            window.Show();
            Settle(window);

            var titles = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("bandtitle")).ToList();
            Assert.Equal(3, titles.Count);
            Assert.All(titles, t => Assert.Equal(13, t.FontSize));

            var bands = window.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("groupband")).ToList();
            Assert.All(bands, band => Assert.True(band.Bounds.Height >= 34, $"band is {band.Bounds.Height} px high"));

            var sections = bands.Select(b => b.FindAncestorOfType<StackPanel>(includeSelf: false)!).ToList();
            for (var i = 1; i < sections.Count; i++)
                Assert.Equal(18, sections[i].Bounds.Top - sections[i - 1].Bounds.Bottom);

            window.Close();
            vm.Dispose();
        }
    }
}
