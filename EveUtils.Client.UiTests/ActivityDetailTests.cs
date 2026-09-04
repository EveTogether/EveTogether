using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EveUtils.Client.Dialogs;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Client.Views;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Market.Entities;
using EveUtils.Shared.Modules.Market.Repositories;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Queries;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using ICqrsDispatcher = EveUtils.Shared.Cqrs.IDispatcher;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-162: the detail of one saved activity. One counter-proof per acceptance criterion, taken from the ticket
/// itself — each was shown red before the screen existed.
/// </summary>
public sealed class ActivityDetailTests
{
    private static readonly DateTime StartedAtUtc = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FakeDisplay : IModuleHostDisplay
    {
        public bool IsFloating { get; set; }
        public ObservableCollection<HostTab> HostTabs { get; } = new();
        public HostTab? SelectedHostTab { get; set; }
    }

    /// <summary>AC-1, mission half: a mission names its agent and its level and shows REWARDS, and carries no
    /// BOUNTY or LOOT section. Counter-proof: give every kind the same fixed block of sections and this goes red on
    /// a visible BOUNTY heading.</summary>
    [AvaloniaFact]
    public async Task Mission_ShowsAgentAndRewards_AndNoBountyOrLootSection()
    {
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = instance.Services.GetRequiredService<ICqrsDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Mission, StartedAtUtc,
            4022, "Paragon Requests: Ships for Tips", 30000142,
            SiteTypeSource: SiteTypeSource.Mission, AgentId: 3018841, MissionLevel: 2), cancellationToken);
        await dispatcher.Send(new SaveRunCommand(started.Value, StartedAtUtc.AddMinutes(8), StartedAtUtc.AddMinutes(9),
            [], [], [],
            [new RunParameterInput { ParameterKey = RunParameterKey.LoyaltyPoints, TypedValue = "1,240", Amount = 1_240m, ObservedAtUtc = StartedAtUtc }]),
            cancellationToken);

        List<string> texts = await _RenderAsync(instance, cancellationToken);

        Assert.Contains(texts, text => text == "agent 3018841");
        Assert.Contains(texts, text => text == "level 2");
        Assert.Contains(texts, text => text == "REWARDS");
        Assert.DoesNotContain(texts, text => text == "BOUNTY");
        Assert.DoesNotContain(texts, text => text == "LOOT");
    }

    /// <summary>AC-1, anomaly half: a site shows ENEMIES, BOUNTY and LOOT and carries no agent row. Counter-proof:
    /// the same fixed block of sections for every kind puts an agent row on a site, and this goes red.</summary>
    [AvaloniaFact]
    public async Task Site_ShowsEnemiesBountyAndLoot_AndNoAgentRow()
    {
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = instance.Services.GetRequiredService<ICqrsDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await _SaveSiteRunAsync(dispatcher, 90000001, groupCode: null, cancellationToken: cancellationToken);

        List<string> texts = await _RenderAsync(instance, cancellationToken);

        Assert.Contains(texts, text => text == "ENEMIES");
        Assert.Contains(texts, text => text == "BOUNTY");
        Assert.Contains(texts, text => text == "LOOT");
        Assert.DoesNotContain(texts, text => text == "AGENT");
        Assert.DoesNotContain(texts, text => text.StartsWith("agent "));
    }

    /// <summary>AC-2: the section a kind has is drawn even when it is empty, and then says why rather than showing
    /// a figure. Counter-proof, both halves: format the totals as <c>?? 0</c> and this goes red on "0 ISK"; drop
    /// the section entirely and it goes red on the missing LOOT heading, because then nothing on screen tells
    /// "nothing was captured" apart from "this kind has no loot".</summary>
    [AvaloniaFact]
    public async Task SiteWithoutLootCapture_SaysWhyThereIsNothing_AndShowsNoZero()
    {
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = instance.Services.GetRequiredService<ICqrsDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await _SaveSiteRunAsync(dispatcher, 90000001, groupCode: null, cancellationToken: cancellationToken);

        List<string> texts = await _RenderAsync(instance, cancellationToken);

        Assert.Contains(texts, text => text == "LOOT");
        Assert.Contains(texts, text => text.StartsWith("No loot capture was recorded"));
        Assert.DoesNotContain(texts, text => text.Contains("0 ISK"));
    }

    /// <summary>AC-3: a loot line whose clipboard column disagrees with the market is shown and counted at the
    /// market price, and a line the lookup has no price for is counted separately rather than valued at nothing.
    /// Counter-proof: bind the line to <see cref="RunLootEntryDto.ClipboardPrice"/> — the way the running run's
    /// LOOT list still does — and the copied 999,999,999 shows up, which this forbids.</summary>
    [AvaloniaFact]
    public async Task LootLine_IsValuedFromThePriceLookup_NotFromTheClipboardColumn()
    {
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = instance.Services.GetRequiredService<ICqrsDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await instance.Services.GetRequiredService<IMarketPriceRepository>().ReplaceAllAsync(
            [new LocalMarketPrice { TypeId = 34, AveragePrice = 100, AdjustedPrice = 100, UpdatedAt = DateTimeOffset.UtcNow }],
            cancellationToken);
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Site, StartedAtUtc,
            1234, "Homefront", 30000142), cancellationToken);
        await dispatcher.Send(new SaveRunCommand(started.Value, StartedAtUtc.AddMinutes(15), StartedAtUtc.AddMinutes(16),
            [new RunLootCaptureInput
            {
                CapturedAtUtc = StartedAtUtc.AddMinutes(10), Source = LootCaptureSource.Clipboard,
                Entries =
                [
                    new RunLootEntryInput { ItemTypeId = 34, Name = "Tritanium", Quantity = 3, ClipboardPrice = 999_999_999m, LootKind = LootKind.Gained },
                    new RunLootEntryInput { ItemTypeId = 35, Name = "Pyerite", Quantity = 1, ClipboardPrice = 12m, LootKind = LootKind.Gained }
                ]
            }], [], [], []), cancellationToken);

        List<string> texts = await _RenderAsync(instance, cancellationToken);

        Assert.Contains(texts, text => text == $"{300m:N2} ISK");
        Assert.DoesNotContain(texts, text => text.Contains("999"));
        Assert.Contains(texts, text => text == "no price");
        Assert.Contains(texts, text => text.StartsWith("1 line has no price"));
    }

    /// <summary>AC-4: an excluded capture keeps its row and counts towards nothing. Counter-proof: filter excluded
    /// captures out of the list — the total still adds up, and this goes red because the capture is gone. Leaving
    /// out is not the same as not counting.</summary>
    [AvaloniaFact]
    public async Task ExcludedCapture_StaysOnScreen_AndDoesNotCount()
    {
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = instance.Services.GetRequiredService<ICqrsDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await instance.Services.GetRequiredService<IMarketPriceRepository>().ReplaceAllAsync(
            [new LocalMarketPrice { TypeId = 34, AveragePrice = 100, AdjustedPrice = 100, UpdatedAt = DateTimeOffset.UtcNow }],
            cancellationToken);
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Site, StartedAtUtc,
            1234, "Homefront", 30000142), cancellationToken);
        RunLootCaptureInput CaptureAt(int minute) => new()
        {
            CapturedAtUtc = StartedAtUtc.AddMinutes(minute), Source = LootCaptureSource.Clipboard, ContentHash = "ABC",
            Entries = [new RunLootEntryInput { ItemTypeId = 34, Name = "Tritanium", Quantity = 3, LootKind = LootKind.Gained }]
        };
        await dispatcher.Send(new SaveRunCommand(started.Value, StartedAtUtc.AddMinutes(15), StartedAtUtc.AddMinutes(16),
            [CaptureAt(10), CaptureAt(11)], [], [], []), cancellationToken);
        Result<RunLootOverview> loot = await dispatcher.Query(new GetRunLootQuery(started.Value), cancellationToken);
        RunLootCaptureDto repeat = _Value(loot).Captures.OrderBy(capture => capture.CapturedAtUtc).Last();
        await dispatcher.Send(new SetRunLootCaptureExclusionCommand(repeat.CaptureId, IsExcluded: true), cancellationToken);

        List<string> texts = await _RenderAsync(instance, cancellationToken);

        Assert.Contains(texts, text => text == $"{300m:N2} ISK");            // 100 x 3 once, not twice
        Assert.Contains(texts, text => text.StartsWith("excluded"));
        Assert.Equal(2, texts.Count(text => text == "Tritanium"));           // both captures still listed
    }

    /// <summary>AC-5: two runs in one activity that each sighted the same enemy type stay two rows, each with its
    /// own first/last window. Counter-proof: group by enemy type alone and there is one row, with the later
    /// sighting silently overwriting the earlier one's window.</summary>
    [AvaloniaFact]
    public async Task SameEnemyTypeOnTwoRuns_StaysTwoRowsWithTheirOwnWindows()
    {
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = instance.Services.GetRequiredService<ICqrsDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await _SaveSiteRunAsync(dispatcher, 90000001, "HF-7QK2", cancellationToken,
            enemies: [new RunEnemyObservationInput { Count = 2, EnemyTypeId = 111, EnemyName = "Centii Scavenger", FirstObservedAtUtc = StartedAtUtc, LastObservedAtUtc = StartedAtUtc.AddMinutes(1) }]);
        await _SaveSiteRunAsync(dispatcher, 90000002, "HF-7QK2", cancellationToken,
            enemies: [new RunEnemyObservationInput { Count = 3, EnemyTypeId = 111, EnemyName = "Centii Scavenger", FirstObservedAtUtc = StartedAtUtc.AddMinutes(4), LastObservedAtUtc = StartedAtUtc.AddMinutes(5) }]);

        List<string> texts = await _RenderAsync(instance, cancellationToken);

        Assert.Equal(2, texts.Count(text => text == "Centii Scavenger"));
        Assert.Contains(texts, text => text == _Window(0, 1));
        Assert.Contains(texts, text => text == _Window(4, 5));
    }

    /// <summary>AC-6: a run whose times were corrected by hand reads differently from one that was measured. The
    /// corrected moments are written over the start and stop themselves, so the duration beside it cannot say which
    /// it is. Counter-proof: show both runs identically and the "corrected by hand" assertion goes red.</summary>
    [AvaloniaFact]
    public async Task CorrectedRun_ReadsDifferentlyFromAMeasuredOne()
    {
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = instance.Services.GetRequiredService<ICqrsDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await _SaveSiteRunAsync(dispatcher, 90000001, "HF-7QK2", cancellationToken);
        Result<Guid> corrected = await dispatcher.Send(new StartRunCommand(90000002, ActivityKind.Site, StartedAtUtc,
            1234, "Homefront", 30000142, "HF-7QK2"), cancellationToken);
        await dispatcher.Send(new SaveRunCommand(corrected.Value, StartedAtUtc.AddMinutes(15), StartedAtUtc.AddMinutes(16),
            [], [], [], [], StartedAtUtc.AddMinutes(-1), StartedAtUtc.AddMinutes(16)), cancellationToken);

        List<string> texts = await _RenderAsync(instance, cancellationToken);

        Assert.Contains(texts, text => text == "measured");
        Assert.Contains(texts, text => text.StartsWith("corrected by hand at"));
    }

    /// <summary>AC-7: the fleet section reports the real headcount and says the names are what is missing, rather
    /// than standing empty. Counter-proof, both halves: an empty list with no line goes red on the missing
    /// sentence, and counting the (never filled) name list instead of the summary's own participant count reads
    /// "0 participants" where six flew it.</summary>
    [AvaloniaFact]
    public async Task FleetSection_ReportsTheRealHeadcount_AndSaysTheNamesAreMissing()
    {
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = instance.Services.GetRequiredService<ICqrsDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        for (long characterId = 90000001; characterId <= 90000006; characterId++)
            await _SaveSiteRunAsync(dispatcher, characterId, "HF-7QK2", cancellationToken);

        List<string> texts = await _RenderAsync(instance, cancellationToken);

        Assert.Contains(texts, text => text == "6 participants");
        Assert.Contains(texts, text => text.StartsWith("Participant names are not recorded yet"));
    }

    /// <summary>AC-8, first half: nothing falls outside the module host's own 758px docked width, and the same
    /// layout still holds at a wide floating width. Counter-proof: the wide render must pass — a check that goes
    /// green at both widths without the layout being fluid is not measuring the layout.</summary>
    [AvaloniaTheory]
    [InlineData(758)]
    [InlineData(1180)]
    public async Task Detail_FitsWithoutOverflowingItsWidth(double width)
    {
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = instance.Services.GetRequiredService<ICqrsDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await _SaveSiteRunAsync(dispatcher, 90000001, "HF-7QK2", cancellationToken,
            enemies: [new RunEnemyObservationInput { Count = 41, EnemyTypeId = 111, EnemyName = "Centii Scavenger", FirstObservedAtUtc = StartedAtUtc, LastObservedAtUtc = StartedAtUtc.AddMinutes(1) }]);

        (_, Window root) = await _PresentAsync(instance, width, cancellationToken);

        foreach (Control control in root.GetVisualDescendants().OfType<Control>()
                     .Where(candidate => candidate is TextBlock or Button && candidate.IsEffectivelyVisible))
        {
            Point topLeft = control.TranslatePoint(default, root) ?? default;
            Assert.True(topLeft.X + control.Bounds.Width <= root.Bounds.Width + 0.5,
                $"{control.GetType().Name} overflows at width {width}: right edge " +
                $"{topLeft.X + control.Bounds.Width:F1} > {root.Bounds.Width}");
        }
    }

    /// <summary>AC-8, second half: docked and floating are the very same <c>Content</c> instance, because the
    /// module host moves it between the two. Counter-proof: build a second layout for the floating case and this
    /// goes red — two layouts is the problem, not the fix.</summary>
    [AvaloniaFact]
    public async Task Detail_IsTheSameContentDockedAndFloating()
    {
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = instance.Services.GetRequiredService<ICqrsDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await _SaveSiteRunAsync(dispatcher, 90000001, groupCode: null, cancellationToken: cancellationToken);
        ActivityDetailWindow window = await _WindowAsync(instance, 758, cancellationToken);

        var display = new FakeDisplay();
        var host = new ModuleHostService();
        host.SetOwner(new Window());
        host.SetHost(display);
        host.Open(window, "ACTIVITY", "runs", "activity-detail");
        var docked = (Control)Assert.Single(display.HostTabs).Content!;

        display.IsFloating = true;
        host.SwitchMode();

        Assert.Same(docked, window.Content);
    }

    private static async Task<List<string>> _RenderAsync(TestClientInstance instance, CancellationToken cancellationToken)
    {
        (_, Window root) = await _PresentAsync(instance, 758, cancellationToken);
        return RenderedText.VisibleTexts(root);
    }

    // The control tree the operator sees when the module is docked: the host lifts window.Content out and reparents
    // it, so the assertions run against that content and never against a window that is never shown.
    private static async Task<(ActivityDetailWindow Window, Window Root)> _PresentAsync(
        TestClientInstance instance, double width, CancellationToken cancellationToken)
    {
        ActivityDetailWindow window = await _WindowAsync(instance, width, cancellationToken);
        var display = new FakeDisplay();
        var host = new ModuleHostService();
        host.SetOwner(new Window());
        host.SetHost(display);
        host.Open(window, "ACTIVITY", "runs", "activity-detail");

        var content = (Control)Assert.Single(display.HostTabs).Content!;
        var root = new Window { Width = width, Height = 1400, Content = content };
        root.Show();
        Dispatcher.UIThread.RunJobs();
        root.UpdateLayout();
        return (window, root);
    }

    private static async Task<ActivityDetailWindow> _WindowAsync(
        TestClientInstance instance, double width, CancellationToken cancellationToken)
    {
        ICqrsDispatcher dispatcher = instance.Services.GetRequiredService<ICqrsDispatcher>();
        await dispatcher.Send(new RebuildActivitySummariesCommand(), cancellationToken);
        Result<IReadOnlyList<ActivityOverviewRowDto>> overview =
            await dispatcher.Query(new GetActivityOverviewQuery(), cancellationToken);
        ActivityOverviewRowDto row = Assert.Single(_Value(overview));

        var viewModel = new ActivityDetailViewModel(dispatcher, row.ActivitySummaryId,
            instance.Services.GetRequiredService<IMarketPriceRepository>());
        await viewModel.LoadAsync(cancellationToken);
        return new ActivityDetailWindow(viewModel) { Width = width, Height = 1400 };
    }

    private static async Task _SaveSiteRunAsync(ICqrsDispatcher dispatcher, long characterId, string? groupCode,
        CancellationToken cancellationToken, IReadOnlyList<RunEnemyObservationInput>? enemies = null)
    {
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(characterId, ActivityKind.Site, StartedAtUtc,
            1234, "Homefront", 30000142, groupCode), cancellationToken);
        await dispatcher.Send(new SaveRunCommand(started.Value, StartedAtUtc.AddMinutes(15),
            StartedAtUtc.AddMinutes(16), [], [], enemies ?? [], []), cancellationToken);
    }

    private static string _Window(int firstMinute, int lastMinute) =>
        $"{StartedAtUtc.AddMinutes(firstMinute).ToLocalTime():HH:mm:ss} – " +
        $"{StartedAtUtc.AddMinutes(lastMinute).ToLocalTime():HH:mm:ss}";

    private static T _Value<T>(Result<T> result)
    {
        Assert.True(result.IsSuccess, result.IsSuccess ? "" : result.Messages[0].Text);
        return result.Value!;
    }
}
