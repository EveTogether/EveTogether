using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EveUtils.Client.Controls;
using EveUtils.Client.Fleet;
using EveUtils.Client.ViewModels;
using EveUtils.Client.Views;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Modules.Fleet.Metrics;
using EveUtils.Shared.Modules.Settings.Dtos;
using EveUtils.Shared.Modules.Settings.Queries;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using ICqrsDispatcher = EveUtils.Shared.Cqrs.IDispatcher;

namespace EveUtils.Client.UiTests;

/// <summary>
/// The fleet-metrics window trades detail per member for members per screen: list (everything), grid (cards, no
/// cap/neut/bounty figures) and compact (one line, no graph either). The chosen density is a whole-install
/// preference, so it survives closing the window and the client. Whatever the density, the commander-presence badge
/// stays in the header.
/// </summary>
public class FleetMetricsLayoutTests
{
    private const int Commander = 90250177;
    private const int Member = 90250178;
    private const long FleetId = 100;

    private static readonly FleetInfo Op = new(FleetId, "Op", null, FleetVisibility.Public, FleetState.Active, 1,
        null, null, DateTimeOffset.UnixEpoch, FleetActivation.Active);

    private static TestClientInstance CreateInstance() => TestClientInstance.Create(services =>
        services.AddSingleton<IExternalCharacterLookup>(new FakeExternalLookup
        {
            [Commander] = "RaymondKrah",
            [Member] = "Lionear",
        }));

    private static FakeFleetClient Roster() => new()
    {
        Members =
        [
            new FleetMemberInfo(1, Commander, -1, -1, FleetRole.FleetCommander, false),
            new FleetMemberInfo(2, Member, 1, 1, FleetRole.SquadMember, false),
        ],
    };

    // Every render assertion needs the roster pre-fill to have landed, otherwise it asserts against an empty list.
    private static async Task<FleetMetricsViewModel> BuildViewModelAsync(TestClientInstance instance, IFleetClient fleets)
    {
        var vm = new FleetMetricsViewModel(instance.Services, fleets, Op);
        for (var i = 0; i < 100 && vm.Members.Count < 2; i++)
            await Task.Delay(20);
        Assert.Equal(2, vm.Members.Count);
        return vm;
    }

    // A window on a fleet whose members share a location and a bounty, so every field the list shows has something
    // to show and its absence in a denser layout means the layout dropped it, not that the data was missing.
    private static async Task<(FleetMetricsWindow Window, FleetMetricsViewModel Vm)> ShowWindowAsync(
        TestClientInstance instance, FleetMetricsLayout layout)
    {
        var vm = await BuildViewModelAsync(instance, Roster());
        var bus = instance.Services.GetRequiredService<IEventBus>();
        await PublishAsync(bus, vm, MetricKind.Location, "Jita");
        await PublishAsync(bus, vm, MetricKind.Bounty, null, 5_000_000);

        vm.SetLayoutCommand.Execute(layout);

        var window = new FleetMetricsWindow(vm) { Width = 900, Height = 620 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, vm);
    }

    private static async Task PublishAsync(
        IEventBus bus, FleetMetricsViewModel vm, MetricKind kind, string? text, double value = 0)
    {
        foreach (var characterId in new[] { Commander, Member })
            await bus.PublishAsync(new FleetMetricEvent(new MetricSample(characterId, FleetId, kind, value, 0, text)));

        // Samples are routed onto the UI thread, so a published sample is not yet a rendered one.
        for (var i = 0; i < 100 && vm.Members.Any(m => kind is MetricKind.Location ? m.Location is null : m.Bounty == 0); i++)
            await Task.Delay(20);
    }

    // The one ItemsControl that hosts the member rows — found by its source, so the window's own template parts
    // cannot be mistaken for it.
    private static ItemsControl MemberHost(FleetMetricsWindow window, FleetMetricsViewModel vm) =>
        window.GetVisualDescendants().OfType<ItemsControl>().First(c => ReferenceEquals(c.ItemsSource, vm.Members));

    private static IReadOnlyList<string> MemberTexts(FleetMetricsWindow window, FleetMetricsViewModel vm) =>
        MemberHost(window, vm).GetVisualDescendants().OfType<TextBlock>()
            .Select(t => t.Text ?? string.Empty)
            .Where(t => t.Length > 0)
            .ToList();

    // The rendered height of one member's row, margin included — the ItemsControl itself always fills the viewport.
    private static double RowHeight(FleetMetricsWindow window, FleetMetricsViewModel vm)
    {
        Control container = MemberHost(window, vm).ContainerFromIndex(0)
            ?? throw new InvalidOperationException("the member list rendered no rows");
        return container.Bounds.Height;
    }

    private static bool HasFigure(IReadOnlyList<string> texts, string label) =>
        texts.Any(t => t.StartsWith(label, StringComparison.Ordinal));

    private static async Task<string?> ReadStoredLayoutAsync(TestClientInstance instance)
    {
        using var scope = instance.Services.CreateScope();
        IReadOnlyList<SettingDto> settings = await scope.ServiceProvider
            .GetRequiredService<ICqrsDispatcher>().Query(new GetSettingsQuery());
        return settings.FirstOrDefault(s => s.Key == FleetMetricsViewModel.LayoutSettingKey)?.Value;
    }

    [AvaloniaFact]
    public async Task Layout_DefaultsToList_WhenNothingIsStored()
    {
        using var instance = CreateInstance();
        var vm = await BuildViewModelAsync(instance, Roster());

        Assert.Equal(FleetMetricsLayout.List, vm.Layout);
        Assert.True(vm.IsListLayout);
        Assert.True(vm.ShowsGraphs);
        Assert.Null(await ReadStoredLayoutAsync(instance));
    }

    [AvaloniaFact]
    public async Task Layout_IsRestored_ByTheNextWindowOnTheSameInstall()
    {
        using var instance = CreateInstance();
        var first = await BuildViewModelAsync(instance, Roster());

        first.SetLayoutCommand.Execute(FleetMetricsLayout.Compact);
        for (var i = 0; i < 100 && await ReadStoredLayoutAsync(instance) is null; i++)
            await Task.Delay(20);
        Assert.Equal("compact", await ReadStoredLayoutAsync(instance));
        first.Dispose();

        // A second window is what the next session sees: a fresh view-model over the same settings store.
        var second = await BuildViewModelAsync(instance, Roster());
        for (var i = 0; i < 100 && second.Layout is FleetMetricsLayout.List; i++)
            await Task.Delay(20);
        Assert.Equal(FleetMetricsLayout.Compact, second.Layout);
        second.Dispose();
    }

    [AvaloniaFact]
    public async Task ListLayout_ShowsEveryFigure_AndAGraphPerMember()
    {
        using var instance = CreateInstance();
        var (window, vm) = await ShowWindowAsync(instance, FleetMetricsLayout.List);

        var texts = MemberTexts(window, vm);
        Assert.Contains("Lionear", texts);
        Assert.True(HasFigure(texts, "OUT"));
        Assert.True(HasFigure(texts, "IN"));
        Assert.True(HasFigure(texts, "CAP"));
        Assert.True(HasFigure(texts, "NEUT"));
        Assert.Contains(texts, t => t.StartsWith("◉ Jita", StringComparison.Ordinal));
        Assert.Contains(texts, t => t.Contains("ISK", StringComparison.Ordinal));
        Assert.Equal(2, MemberHost(window, vm).GetVisualDescendants().OfType<DpsGraph>().Count());
    }

    [AvaloniaFact]
    public async Task GridLayout_DropsCapNeutAndBounty_ButKeepsIdentityDpsLocationAndTheGraph()
    {
        using var instance = CreateInstance();
        var (window, vm) = await ShowWindowAsync(instance, FleetMetricsLayout.Grid);

        var texts = MemberTexts(window, vm);
        Assert.Contains("Lionear", texts);
        Assert.True(HasFigure(texts, "OUT"));
        Assert.True(HasFigure(texts, "IN"));
        Assert.Contains(texts, t => t.StartsWith("◉ Jita", StringComparison.Ordinal));
        Assert.False(HasFigure(texts, "CAP"));
        Assert.False(HasFigure(texts, "NEUT"));
        Assert.DoesNotContain(texts, t => t.Contains("ISK", StringComparison.Ordinal));

        // The graph carries the cap/neut lines the figures gave up, and the cards sit side by side.
        Assert.Equal(2, MemberHost(window, vm).GetVisualDescendants().OfType<DpsGraph>().Count());
        Assert.Single(MemberHost(window, vm).GetVisualDescendants().OfType<WrapPanel>());
    }

    [AvaloniaFact]
    public async Task CompactLayout_DropsTheGraph_ButKeepsIdentityDpsAndLocation()
    {
        using var instance = CreateInstance();
        var (window, vm) = await ShowWindowAsync(instance, FleetMetricsLayout.Compact);

        var texts = MemberTexts(window, vm);
        Assert.Contains("Lionear", texts);
        Assert.Contains("RaymondKrah", texts);
        Assert.True(HasFigure(texts, "OUT"));
        Assert.True(HasFigure(texts, "IN"));
        Assert.Contains(texts, t => t.StartsWith("◉ Jita", StringComparison.Ordinal));
        Assert.False(HasFigure(texts, "CAP"));
        Assert.False(HasFigure(texts, "NEUT"));
        Assert.DoesNotContain(texts, t => t.Contains("ISK", StringComparison.Ordinal));
        Assert.Empty(MemberHost(window, vm).GetVisualDescendants().OfType<DpsGraph>());

        // No graphs left to explain, so the line legend goes with them.
        Assert.False(vm.ShowsGraphs);
    }

    // Density is the whole point of the compact layout, so measure the rendered row rather than trust the template.
    [AvaloniaFact]
    public async Task CompactLayout_FitsFarMoreMembersOnScreen_ThanTheListLayout()
    {
        using var instance = CreateInstance();
        var (window, vm) = await ShowWindowAsync(instance, FleetMetricsLayout.List);
        double listRow = RowHeight(window, vm);

        vm.SetLayoutCommand.Execute(FleetMetricsLayout.Compact);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        double compactRow = RowHeight(window, vm);

        Assert.True(compactRow < listRow / 3,
            $"a compact row should take under a third of a list row (list {listRow}, compact {compactRow})");
    }

    [AvaloniaTheory]
    [InlineData(FleetMetricsLayout.List)]
    [InlineData(FleetMetricsLayout.Grid)]
    [InlineData(FleetMetricsLayout.Compact)]
    public async Task CommanderBadge_StaysInTheHeader_InEveryLayout(FleetMetricsLayout layout)
    {
        using var instance = CreateInstance();
        var (window, vm) = await ShowWindowAsync(instance, layout);

        var badge = Assert.Single(window.GetVisualDescendants().OfType<Border>(), b => b.Classes.Contains("chip"));
        var text = Assert.IsType<TextBlock>(badge.Child);
        Assert.Equal("◉ 2/2 WITH FC", text.Text);
        Assert.True(vm.CommanderPresence.IsComplete);
    }

    [AvaloniaTheory]
    [InlineData(FleetMetricsLayout.Grid, "cap, neut and bounty")]
    [InlineData(FleetMetricsLayout.Compact, "Graphs and the cap, neut and bounty")]
    public async Task LayoutHint_NamesWhatTheDensityDrops(FleetMetricsLayout layout, string dropped)
    {
        using var instance = CreateInstance();
        var vm = await BuildViewModelAsync(instance, Roster());

        vm.SetLayoutCommand.Execute(layout);

        Assert.Contains(dropped, vm.LayoutHint, StringComparison.Ordinal);
        vm.Dispose();
    }
}
