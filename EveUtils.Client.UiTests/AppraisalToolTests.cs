using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Theming;
using EveUtils.Client.ViewModels;
using EveUtils.Client.Views;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Market.Entities;
using EveUtils.Shared.Modules.Market.Repositories;
using EveUtils.Shared.Modules.Market.Services;
using EveUtils.Shared.Modules.Sde;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// The Appraisal tool as the user meets it (ET-83): a listing pasted, valued and totalled, a name the SDE cannot
/// place kept in sight rather than dropped, an unfilled price cache said out loud instead of shown as a total of
/// zero, and the screen itself rendered in both shells. The price source behind it is the real
/// <c>MarketPriceAppraisalProvider</c> over a real (scratch) price cache.
/// </summary>
public sealed class AppraisalToolTests
{
    // A hangar of minerals: names outnumber their shared group, which is what the inventory parser needs to be sure
    // which column is the name. Isogen is deliberately left out of the price cache below.
    private const string MineralPaste =
        "Tritanium\t1,000,000\tMineral\t10,000.00 m3\r\n" +
        "Pyerite\t250,000\tMineral\t2,500.00 m3\r\n" +
        "Mexallon\t40,000\tMineral\t400.00 m3\r\n" +
        "Isogen\t5,000\tMineral\t50.00 m3";

    private static FakeSdeAccessor _Minerals() => new FakeSdeAccessor()
        .Add(34, "Tritanium", 18, 4)
        .Add(35, "Pyerite", 18, 4)
        .Add(36, "Mexallon", 18, 4)
        .Add(37, "Isogen", 18, 4);

    private static TestClientInstance _NewInstance(ISdeAccessor sde) =>
        TestClientInstance.Create(services => services.AddSingleton(sde));

    /// <summary>Fills the client's price cache the same way the hourly ESI refresh does.</summary>
    private static async Task _CachePricesAsync(TestClientInstance instance, DateTimeOffset updatedAt,
        params (int TypeId, double Average)[] prices) =>
        await instance.Services.GetRequiredService<IMarketPriceRepository>().ReplaceAllAsync(
            [.. prices.Select(price => new LocalMarketPrice
            {
                TypeId = price.TypeId,
                AveragePrice = price.Average,
                AdjustedPrice = price.Average,
                UpdatedAt = updatedAt
            })]);

    private static AppraisalViewModel _BuildTool(TestClientInstance instance) =>
        new(instance.Services.GetRequiredService<IEnumerable<IAppraisalProvider>>(),
            instance.Services.GetRequiredService<ISdeAccessor>());

    // ── Valuing a paste ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The whole path in one go: the inventory parser reads the paste, the SDE turns names into type ids, and the
    /// cached averages turn those into a total. Every figure is checked, because a tool that totals the wrong
    /// column still looks like it worked.
    /// </summary>
    [AvaloniaFact]
    public async Task Tool_ValuesAPastedListing_AndTotalsIt()
    {
        using var instance = _NewInstance(_Minerals());
        await _CachePricesAsync(instance, new DateTimeOffset(2026, 8, 31, 9, 30, 0, TimeSpan.Zero),
            (34, 5), (35, 10), (36, 80), (37, 40));
        var tool = _BuildTool(instance);
        tool.PasteText = MineralPaste;

        await tool.AppraiseCommand.ExecuteAsync(null);

        Assert.False(tool.StatusIsError);
        Assert.Equal(4, tool.Rows.Count);
        Assert.False(tool.HasUnresolved);

        var tritanium = tool.Rows.Single(row => row.Name == "Tritanium");
        Assert.Equal(1_000_000, tritanium.Quantity);
        Assert.Equal("1,000,000", tritanium.QuantityDisplay);
        Assert.Equal(5, tritanium.UnitPrice);
        Assert.Equal(5_000_000, tritanium.Total);
        Assert.Equal("5 M ISK", tritanium.TotalDisplay);

        // 1,000,000×5 + 250,000×10 + 40,000×80 + 5,000×40 = 10,900,000
        Assert.Equal("10.9 M ISK", tool.TotalDisplay);
        Assert.Equal("ITEMS (4)", tool.RowsHeader);
    }

    /// <summary>The header says what the figures are and how old they are — the readout must not pass a global
    /// average off as a Jita quote, and must not present last week's snapshot as today's.</summary>
    [AvaloniaFact]
    public async Task Tool_NamesWhatThePricesAre_AndWhenTheyWereCached()
    {
        using var instance = _NewInstance(_Minerals());
        await _CachePricesAsync(instance, new DateTimeOffset(2026, 8, 31, 9, 30, 0, TimeSpan.Zero), (34, 5));
        var tool = _BuildTool(instance);
        tool.PasteText = "Tritanium\t100";

        await tool.AppraiseCommand.ExecuteAsync(null);

        Assert.Contains("not a Jita buy or sell quote", tool.PricingBasis);
        Assert.Contains("2026-08-31 09:30 UTC", tool.PricingBasis);
    }

    /// <summary>A type the cache has no price for is a row without a price, not a missing row and not a failure —
    /// and the status says how many of them there were, so the total is not read as complete.</summary>
    [AvaloniaFact]
    public async Task Tool_ShowsAnItemWithNoCachedPrice_AndSaysHowManyCarryNone()
    {
        using var instance = _NewInstance(_Minerals());
        await _CachePricesAsync(instance, DateTimeOffset.UtcNow, (34, 5), (35, 10), (36, 80));
        var tool = _BuildTool(instance);
        tool.PasteText = MineralPaste;

        await tool.AppraiseCommand.ExecuteAsync(null);

        var isogen = tool.Rows.Single(row => row.Name == "Isogen");
        Assert.Equal(0, isogen.UnitPrice);
        Assert.Equal("— ISK", isogen.UnitPriceDisplay);
        Assert.Contains("1 of them carry no price", tool.Status);
        Assert.False(tool.StatusIsError);
        Assert.Equal("10.7 M ISK", tool.TotalDisplay);   // the priced three, and nothing invented for the fourth
    }

    /// <summary>A name the SDE does not know gets its own list. Dropping it silently would leave a total that is
    /// short by an unknown amount and looks complete.</summary>
    [AvaloniaFact]
    public async Task Tool_ListsNamesTheSdeCannotPlace_RatherThanDroppingThemFromTheTotal()
    {
        using var instance = _NewInstance(_Minerals());
        await _CachePricesAsync(instance, DateTimeOffset.UtcNow, (34, 5));
        var tool = _BuildTool(instance);
        tool.PasteText = "Tritanium\t1,000,000\tMineral\t10,000.00 m3\r\n" +
                         "Spodumain Chunk\t12\tMineral\t100.00 m3";

        await tool.AppraiseCommand.ExecuteAsync(null);

        Assert.Equal(["Spodumain Chunk"], tool.Unresolved);
        Assert.True(tool.HasUnresolved);
        Assert.Equal("NOT RECOGNISED (1)", tool.UnresolvedHeader);
        Assert.Single(tool.Rows);
        Assert.Contains("1 name(s) were not recognised", tool.Status);
    }

    // ── When there is nothing to answer with ─────────────────────────────────────────────────────

    /// <summary>
    /// An empty price cache is the state a fresh install is in for up to an hour. A total of zero reads as an
    /// answer, so the tool says the cache is empty instead — the distinction the provider makes with its own
    /// count, separate from "these particular items have no price".
    /// </summary>
    [AvaloniaFact]
    public async Task Tool_SaysThePriceCacheIsEmpty_InsteadOfTotallingToZero()
    {
        using var instance = _NewInstance(_Minerals());
        var tool = _BuildTool(instance);
        tool.PasteText = MineralPaste;

        await tool.AppraiseCommand.ExecuteAsync(null);

        Assert.True(tool.StatusIsError);
        Assert.Contains("No market prices have been cached yet", tool.Status);
        Assert.Empty(tool.Rows);
        Assert.Equal("— ISK", tool.TotalDisplay);
    }

    /// <summary>Without an SDE there are no type ids to look prices up by, and the tool says which of the two
    /// things is missing rather than reporting every pasted name as unknown.</summary>
    [AvaloniaFact]
    public async Task Tool_SaysTheSdeIsMissing_RatherThanCallingEveryNameUnknown()
    {
        using var instance = _NewInstance(_Minerals().Offline());
        await _CachePricesAsync(instance, DateTimeOffset.UtcNow, (34, 5));
        var tool = _BuildTool(instance);
        tool.PasteText = MineralPaste;

        await tool.AppraiseCommand.ExecuteAsync(null);

        Assert.True(tool.StatusIsError);
        Assert.Contains("SDE is not loaded", tool.Status);
        Assert.Empty(tool.Unresolved);
    }

    /// <summary>
    /// The multibuy format ("Tritanium 100") is a later ticket, and it is the format a user is most likely to try
    /// first. It reads as one column of names that are not types, so the tool has to say that in so many words
    /// instead of leaving an empty screen behind.
    /// </summary>
    [AvaloniaFact]
    public async Task Tool_SaysAMultibuyListIsNotReadYet()
    {
        using var instance = _NewInstance(_Minerals());
        await _CachePricesAsync(instance, DateTimeOffset.UtcNow, (34, 5));
        var tool = _BuildTool(instance);
        tool.PasteText = "Tritanium 1000\r\nPyerite 500";

        await tool.AppraiseCommand.ExecuteAsync(null);

        Assert.True(tool.StatusIsError);
        Assert.Contains("multibuy", tool.Status);
        Assert.Equal(["Tritanium 1000", "Pyerite 500"], tool.Unresolved);
        Assert.Empty(tool.Rows);
    }

    /// <summary>Text that is not a listing at all leaves the previous result standing nowhere: nothing is appraised
    /// and the reason is on screen.</summary>
    [AvaloniaFact]
    public async Task Tool_RefusesTextThatIsNotAListing_AndClearsWhatWasThere()
    {
        using var instance = _NewInstance(_Minerals());
        await _CachePricesAsync(instance, DateTimeOffset.UtcNow, (34, 5));
        var tool = _BuildTool(instance);

        tool.PasteText = "Tritanium\t1,000,000";
        await tool.AppraiseCommand.ExecuteAsync(null);
        Assert.Single(tool.Rows);

        tool.PasteText = "one\ttwo\r\nragged";   // uneven columns: the parser refuses to guess
        await tool.AppraiseCommand.ExecuteAsync(null);

        Assert.True(tool.StatusIsError);
        Assert.Contains("does not read as an inventory listing", tool.Status);
        Assert.Single(tool.Rows);   // the refusal changes nothing; what was valued is still what is on screen

        tool.ClearCommand.Execute(null);
        Assert.Empty(tool.Rows);
        Assert.Equal(string.Empty, tool.PasteText);
        Assert.Equal("— ISK", tool.TotalDisplay);
        Assert.False(tool.StatusIsError);
    }

    /// <summary>An empty box is not a question, so the button that would ask it is off.</summary>
    [AvaloniaFact]
    public void Tool_CannotAppraiseAnEmptyBox()
    {
        using var instance = _NewInstance(_Minerals());
        var tool = _BuildTool(instance);

        Assert.False(tool.AppraiseCommand.CanExecute(null));
        tool.PasteText = "Tritanium\t1";
        Assert.True(tool.AppraiseCommand.CanExecute(null));
    }

    // ── The provider seam ────────────────────────────────────────────────────────────────────────

    /// <summary>A second price source lands in the tool by being registered, and only then is there a picker. This
    /// is the whole of what the tool knows about providers being plural.</summary>
    [AvaloniaFact]
    public void ProviderPicker_StaysHiddenWithOneSource_AndAppearsWithASecond()
    {
        using var instance = _NewInstance(_Minerals());
        var single = _BuildTool(instance);

        Assert.Single(single.Providers);
        Assert.False(single.ShowProviderPicker);
        Assert.Equal("market-prices", single.SelectedProvider!.Id);

        var installed = instance.Services.GetRequiredService<IEnumerable<IAppraisalProvider>>();
        var both = new AppraisalViewModel([.. installed, new StubProvider()],
            instance.Services.GetRequiredService<ISdeAccessor>());

        Assert.Equal(2, both.Providers.Count);
        Assert.True(both.ShowProviderPicker);
    }

    /// <summary>The chosen source is the one that is asked — and a source quoting both sides of the market fills
    /// buy and sell beside the estimate, which the model has carried since the first line of it.</summary>
    [AvaloniaFact]
    public async Task Tool_AsksTheSelectedSource_AndCarriesItsBuyAndSellFigures()
    {
        using var instance = _NewInstance(_Minerals());
        var stub = new StubProvider();
        var tool = new AppraisalViewModel([stub], instance.Services.GetRequiredService<ISdeAccessor>())
        {
            PasteText = "Tritanium\t1,000"
        };

        await tool.AppraiseCommand.ExecuteAsync(null);

        var line = Assert.Single(stub.Asked);
        Assert.Equal(34, line.TypeId);
        Assert.Equal(1000, line.Quantity);
        Assert.Contains("Stub quotes", tool.PricingBasis);
        Assert.Equal("2 M ISK", tool.TotalDisplay);
    }

    /// <summary>A price source that cannot answer reports why, and the screen shows that rather than a total.</summary>
    [AvaloniaFact]
    public async Task Tool_ReportsWhyASourceCouldNotAnswer()
    {
        using var instance = _NewInstance(_Minerals());
        var tool = new AppraisalViewModel([new StubProvider { Failure = "The market service is unreachable." }],
            instance.Services.GetRequiredService<ISdeAccessor>())
        {
            PasteText = "Tritanium\t1,000"
        };

        await tool.AppraiseCommand.ExecuteAsync(null);

        Assert.True(tool.StatusIsError);
        Assert.Equal("The market service is unreachable.", tool.Status);
        Assert.Empty(tool.Rows);
    }

    /// <summary>A second source resolving names itself reports what it could not read; those names join the ones
    /// this side could not resolve in the one list the user reads.</summary>
    [AvaloniaFact]
    public async Task Unresolved_MergesWhatTheToolCouldNotResolveWithWhatTheSourceCouldNot()
    {
        using var instance = _NewInstance(_Minerals());
        var tool = new AppraisalViewModel([new StubProvider { Unresolved = ["Something Janice refused"] }],
            instance.Services.GetRequiredService<ISdeAccessor>())
        {
            PasteText = "Tritanium\t1,000\tMineral\t10.00 m3\r\nSpodumain Chunk\t2\tMineral\t20.00 m3"
        };

        await tool.AppraiseCommand.ExecuteAsync(null);

        Assert.Equal(["Spodumain Chunk", "Something Janice refused"], tool.Unresolved);
    }

    // ── The Tools menu, and the screen ───────────────────────────────────────────────────────────

    /// <summary>The rail's TOOLS entry reaches the module — the menu is wired, not decorative.</summary>
    [AvaloniaFact]
    public async Task ToolsMenu_OpensTheAppraisalModule()
    {
        using var instance = TestClientInstance.Create(services =>
        {
            services.AddSingleton<ISdeAccessor>(_Minerals());
            services.AddSingleton<IDialogService, RecordingDialogService>();
        });

        var shell = new MainWindowViewModel(instance.Services);
        await shell.LaunchModuleCommand.ExecuteAsync("appraisal");

        var dialogs = (RecordingDialogService)instance.Services.GetRequiredService<IDialogService>();
        Assert.NotNull(dialogs.LastAppraisal);
        Assert.Single(dialogs.LastAppraisal!.Providers);   // the provider arrived through DI, not through a new-up
    }

    /// <summary>
    /// The screen in the three states the operator meets it in: nothing pasted yet, a valued listing, and one with
    /// names it could not place. Rendered rather than only asserted about — green view-model tests have said
    /// nothing about what the operator saw more than once on this project.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("empty", "")]
    [InlineData("valued", MineralPaste)]
    [InlineData("unresolved", MineralPaste + "\r\nSpodumain Chunk\t12\tMineral\t100.00 m3")]
    public async Task AppraisalWindow_Renders(string label, string paste)
    {
        using var instance = _NewInstance(_Minerals());
        await _CachePricesAsync(instance, new DateTimeOffset(2026, 8, 31, 9, 30, 0, TimeSpan.Zero),
            (34, 5), (35, 10), (36, 80));
        instance.Services.GetRequiredService<IThemeService>().Apply(FactionTheme.Gallente);

        var tool = _BuildTool(instance);
        if (paste.Length > 0)
        {
            tool.PasteText = paste;
            await tool.AppraiseCommand.ExecuteAsync(null);
        }

        var window = new AppraisalWindow(tool) { Width = 960, Height = 640 };
        window.Show();
        await _WaitForAsync(() => false, tries: 12);

        var rendered = window.CaptureRenderedFrame();
        Assert.NotNull(rendered);
        rendered!.Save(Path.Combine(_ShotDirectory(), $"eveutils-appraisal-{label}.png"), new PngBitmapEncoderOptions());

        var texts = _VisibleTexts(window);
        Assert.Contains("APPRAISAL", texts);
        Assert.Contains("PASTE", texts);

        if (paste.Length == 0)
        {
            // Nothing appraised is not a total: "ITEMS (0)" beside a "— ISK" reads as an answer to a question that
            // was never asked. Both are bound and both must stay off the screen until there is something to say.
            Assert.Contains("ITEMS", texts);
            Assert.DoesNotContain("ITEMS (0)", texts);
            Assert.DoesNotContain("— ISK", texts);
        }
        else
        {
            // The total and the basis behind it are both actually drawn, not merely bound.
            Assert.Contains("10.7 M ISK", texts);
            Assert.Contains(texts, text => text.Contains("2026-08-31 09:30 UTC"));
            Assert.Contains("Tritanium", texts);
        }
        if (label == "unresolved")
        {
            Assert.Contains("NOT RECOGNISED (1)", texts);
            Assert.Contains("Spodumain Chunk", texts);
        }

        window.Close();
    }

    /// <summary>
    /// Both shells, through the real module host: docked the tool is a tab in the main window, floating it is its
    /// own window, and the open module survives the switch. The grid is the part that has to survive the narrower
    /// docked host — its columns are the answer.
    /// </summary>
    [AvaloniaFact]
    public async Task AppraisalModule_RendersDockedAndFloating()
    {
        using var instance = _NewInstance(_Minerals());
        await _CachePricesAsync(instance, new DateTimeOffset(2026, 8, 31, 9, 30, 0, TimeSpan.Zero),
            (34, 5), (35, 10), (36, 80));
        instance.Services.GetRequiredService<IThemeService>().Apply(FactionTheme.Gallente);

        var shell = new MainWindowViewModel(instance.Services);
        var window = new MainWindow { DataContext = shell, Width = 1100, Height = 720 };
        var dialogs = (DialogService)instance.Services.GetRequiredService<IDialogService>();
        dialogs.SetOwner(window);
        dialogs.SetHost(shell);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        await shell.LaunchModuleCommand.ExecuteAsync("appraisal");
        Assert.True(await _WaitForAsync(() => shell.HostTabs.Count == 1));

        Assert.Equal("APPRAISAL", shell.HostTabs[0].Title);
        Assert.Equal("tools", shell.HostTabs[0].ModuleKey);
        Assert.True(shell.IsToolsActive);

        var tool = Assert.IsType<AppraisalViewModel>(shell.SelectedHostTab!.Content.DataContext);
        tool.PasteText = MineralPaste;
        await tool.AppraiseCommand.ExecuteAsync(null);
        await _WaitForAsync(() => false, tries: 12);

        window.CaptureRenderedFrame()!.Save(
            Path.Combine(_ShotDirectory(), "eveutils-appraisal-docked.png"), new PngBitmapEncoderOptions());

        var texts = _VisibleTexts(window);
        Assert.Contains("ITEMS (4)", texts);
        Assert.Contains("10.7 M ISK", texts);
        // The value columns still show their figures in the narrower host — the reason this screen has a viewport.
        Assert.Contains("Tritanium", texts);
        Assert.Contains("1,000,000", texts);
        Assert.Contains("5 M ISK", texts);

        shell.ToggleDockModeCommand.Execute(null);   // → floating: the tool becomes its own window, not an orphan
        Dispatcher.UIThread.RunJobs();
        Assert.True(shell.IsHomeShown);

        shell.ToggleDockModeCommand.Execute(null);   // → docked again: the same module comes back as a tab
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("APPRAISAL", shell.HostTabs[0].Title);
        Assert.Same(tool, shell.SelectedHostTab!.Content.DataContext);   // and with what it had valued still in it
        window.Close();
    }

    /// <summary>The text actually on the screen — a hidden control is still in the visual tree, so a readout that is
    /// merely bound would pass an assertion that only looked at every <see cref="TextBlock"/>.</summary>
    private static List<string> _VisibleTexts(Visual root) =>
        [.. root.GetVisualDescendants().OfType<TextBlock>()
            .Where(block => block.IsEffectivelyVisible && !string.IsNullOrEmpty(block.Text))
            .Select(block => block.Text!)];

    /// <summary>A second price source, standing in for the one that quotes a real order book: it fills buy and sell
    /// beside the estimate and resolves names itself, which is what the seam exists to allow.</summary>
    private sealed class StubProvider : IAppraisalProvider
    {
        public string Id => "stub";
        public string DisplayName => "Stub market quotes";
        public string? Failure { get; init; }
        public IReadOnlyList<string> Unresolved { get; init; } = [];
        public List<AppraisalLine> Asked { get; } = [];

        public Task<Result<AppraisalOutcome>> AppraiseAsync(
            IReadOnlyCollection<AppraisalLine> lines, CancellationToken cancellationToken = default)
        {
            Asked.AddRange(lines);
            if (Failure is { } failure)
                return Task.FromResult(Result<AppraisalOutcome>.Failure(
                    new ResultMessage(MessageSeverity.Error, MessageCodes.EsiFailed, failure)));

            List<AppraisalRow> rows =
                [.. lines.Select(line => new AppraisalRow(line, new AppraisalPrice(2000, Buy: 1800, Sell: 2200)))];
            return Task.FromResult(Result<AppraisalOutcome>.Success(
                new AppraisalOutcome(rows, Unresolved, "Stub quotes, buy and sell.")));
        }
    }

    private static string _ShotDirectory()
    {
        var directory = Environment.GetEnvironmentVariable("EVEUTILS_SHOT_DIR");
        return string.IsNullOrWhiteSpace(directory) ? Path.GetTempPath() : directory;
    }

    private static async Task<bool> _WaitForAsync(Func<bool> condition, int tries = 150)
    {
        for (var attempt = 0; attempt < tries; attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition()) return true;
            await Task.Delay(20);
        }
        return condition();
    }
}
