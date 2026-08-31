using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
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
public sealed class AppraisalToolTests(ITestOutputHelper output)
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
            instance.Services.GetRequiredService<ISdeAccessor>(),
            instance.Services.GetRequiredService<IDialogService>());

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
        Assert.Equal("5,000,000 ISK", tritanium.TotalDisplay);

        // 1,000,000×5 + 250,000×10 + 40,000×80 + 5,000×40 = 10,900,000. The grand total is written out in full:
        // it is the answer the tool exists to give, and "10.9 M ISK" covers a span of fifty thousand ISK.
        Assert.Equal("10,900,000 ISK", tool.TotalDisplay);
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
        Assert.Equal("10,700,000 ISK", tool.TotalDisplay);   // the priced three, and nothing invented for the fourth
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

    /// <summary>Text that is not a listing at all is refused, and the figures the last paste produced go with it —
    /// a total left standing beside a refusal describes a box that no longer holds what produced it.</summary>
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
        Assert.Empty(tool.Rows);                     // the last paste's figures do not survive a refusal
        Assert.Equal("— ISK", tool.TotalDisplay);
        Assert.Equal(string.Empty, tool.PricingBasis);

        tool.ClearCommand.Execute(null);
        Assert.Equal(string.Empty, tool.PasteText);   // and CLEAR empties the box behind it too
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

    // ── Copying the total (ET-91) ────────────────────────────────────────────────────────────────

    /// <summary>The clipboard gets exactly what the screen shows — grouped and suffixed with "ISK" — because the
    /// operator's use for it is pasting into a chat, not an input field: cleaning it up there would be the wrong
    /// direction. And the user is told it happened: a copy with nothing to show for it is indistinguishable from
    /// one that silently failed.</summary>
    [AvaloniaFact]
    public async Task CopyTotalCommand_CopiesWhatTheScreenShows_AndSaysSo()
    {
        using var instance = TestClientInstance.Create(services =>
        {
            services.AddSingleton<ISdeAccessor>(_Minerals());
            services.AddSingleton<IDialogService, RecordingDialogService>();
        });
        await _CachePricesAsync(instance, new DateTimeOffset(2026, 8, 31, 9, 30, 0, TimeSpan.Zero),
            (34, 5), (35, 10), (36, 80), (37, 40));
        var tool = _BuildTool(instance);
        tool.PasteText = MineralPaste;
        await tool.AppraiseCommand.ExecuteAsync(null);

        Assert.Equal("10,900,000 ISK", tool.TotalDisplay);
        Assert.True(tool.CopyTotalCommand.CanExecute(null));

        await tool.CopyTotalCommand.ExecuteAsync(null);

        var dialogs = (RecordingDialogService)instance.Services.GetRequiredService<IDialogService>();
        Assert.Equal(tool.TotalDisplay, dialogs.LastClipboardText);   // no separate clipboard-only formatting
        Assert.Contains("Copied", tool.Status);
        Assert.False(tool.StatusIsError);
    }

    /// <summary>An empty price cache shows an explicit message instead of a total (ET-83) — copying must not hand
    /// out a silent "0" for it, so the command is off along with the figure it would have copied.</summary>
    [AvaloniaFact]
    public async Task CopyTotalCommand_IsUnavailable_WhenThePriceCacheIsEmpty()
    {
        using var instance = _NewInstance(_Minerals());
        var tool = _BuildTool(instance);
        tool.PasteText = MineralPaste;

        await tool.AppraiseCommand.ExecuteAsync(null);

        Assert.Equal("— ISK", tool.TotalDisplay);
        Assert.False(tool.HasTotal);
        Assert.False(tool.CopyTotalCommand.CanExecute(null));
    }

    /// <summary>Rows that all carry no price total to zero, which reads on screen exactly like the empty state
    /// ("— ISK") — and copying has to agree with what the screen says, not with the number underneath it.</summary>
    [AvaloniaFact]
    public async Task CopyTotalCommand_IsUnavailable_WhenEveryRowIsPriceless()
    {
        using var instance = _NewInstance(_Minerals());
        // Cache is not empty (Pyerite is priced), so this is the "priced nothing pasted" case, not the
        // empty-cache one — and still nothing to copy, because the one pasted row (Tritanium) has no price of
        // its own.
        await _CachePricesAsync(instance, DateTimeOffset.UtcNow, (35, 10));
        var tool = _BuildTool(instance);
        tool.PasteText = "Tritanium\t1,000,000";

        await tool.AppraiseCommand.ExecuteAsync(null);

        Assert.Single(tool.Rows);           // a row exists...
        Assert.Equal("— ISK", tool.TotalDisplay);   // ...but it carries no price, so there is nothing to copy
        Assert.False(tool.CopyTotalCommand.CanExecute(null));
    }

    /// <summary>The empty screen before anything is pasted: same guard, same reason.</summary>
    [AvaloniaFact]
    public void CopyTotalCommand_IsUnavailable_OnTheEmptyScreen()
    {
        using var instance = _NewInstance(_Minerals());
        var tool = _BuildTool(instance);

        Assert.False(tool.HasTotal);
        Assert.False(tool.CopyTotalCommand.CanExecute(null));
    }

    /// <summary>The confirmation actually drawn on screen, not only bound — a copy that only a green assertion can
    /// see is indistinguishable, to the operator, from one that silently did nothing.</summary>
    [AvaloniaFact]
    public async Task CopyTotalCommand_Renders_TheConfirmationInTheStatusBar()
    {
        using var instance = TestClientInstance.Create(services =>
        {
            services.AddSingleton<ISdeAccessor>(_Minerals());
            services.AddSingleton<IDialogService, RecordingDialogService>();
        });
        instance.Services.GetRequiredService<IThemeService>().Apply(FactionTheme.Gallente);
        await _CachePricesAsync(instance, new DateTimeOffset(2026, 8, 31, 9, 30, 0, TimeSpan.Zero),
            (34, 5), (35, 10), (36, 80), (37, 40));
        var tool = _BuildTool(instance);
        tool.PasteText = MineralPaste;
        await tool.AppraiseCommand.ExecuteAsync(null);

        var window = new AppraisalWindow(tool) { Width = 960, Height = 640 };
        window.Show();
        await _WaitForAsync(() => false, tries: 12);

        await tool.CopyTotalCommand.ExecuteAsync(null);
        await _WaitForAsync(() => false, tries: 12);

        window.CaptureRenderedFrame()!.Save(
            Path.Combine(_ShotDirectory(), "eveutils-appraisal-copied.png"), new PngBitmapEncoderOptions());

        Assert.Contains("Copied the total to the clipboard.", _VisibleTexts(window));
        window.Close();
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
        Assert.Equal("2,000,000 ISK", tool.TotalDisplay);
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
            // The total and the basis behind it are both actually drawn, not merely bound — and the total is the
            // whole figure, not a rounded one.
            Assert.Contains("10,700,000 ISK", texts);
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
    /// The total is the answer the tool exists to give, so it is written out in full rather than rounded — "1.4 B
    /// ISK" covers a span of ten million. That makes it far wider than what it replaces, and it sits in an Auto
    /// column beside the heading, so at the window's own minimum width (ET-90 raised it to 760, the narrowest this
    /// screen can be) both still have to hold: the figure whole, the heading not crushed out of the row.
    /// </summary>
    [AvaloniaFact]
    public async Task Total_IsWrittenOutInFull_AndStillFitsBesideTheHeading()
    {
        using var instance = _NewInstance(_Minerals());
        await _CachePricesAsync(instance, new DateTimeOffset(2026, 8, 31, 9, 30, 0, TimeSpan.Zero),
            (34, 1000), (35, 1400), (36, 1125));
        instance.Services.GetRequiredService<IThemeService>().Apply(FactionTheme.Gallente);

        var tool = _BuildTool(instance);
        tool.PasteText = MineralPaste;
        await tool.AppraiseCommand.ExecuteAsync(null);

        // 1,000,000×1,000 + 250,000×1,400 + 40,000×1,125 = 1,395,000,000 — which the compact form would have
        // rounded away to "1.4 B ISK", five million ISK wide.
        Assert.Equal("1,395,000,000 ISK", tool.TotalDisplay);

        var window = new AppraisalWindow(tool) { Width = 760, Height = 520 };   // the window's own minimum (ET-90)
        window.Show();
        await _WaitForAsync(() => false, tries: 12);

        window.CaptureRenderedFrame()!.Save(
            Path.Combine(_ShotDirectory(), "eveutils-appraisal-total-in-full.png"), new PngBitmapEncoderOptions());

        Assert.Contains("1,395,000,000 ISK", _VisibleTexts(window));

        // Bounds, not text: a squeezed TextBlock keeps its full Text and renders an ellipsis, so no string
        // assertion can tell the difference between a figure that fits and one that is cut off.
        var total = _Named(window, "TotalReadout");
        var heading = _Named(window, "ItemsHeading");
        Assert.True(total.Bounds.Width >= total.DesiredSize.Width - 0.5,
            $"the total was squeezed: {total.Bounds.Width} arranged for {total.DesiredSize.Width} desired");
        Assert.True(heading.Bounds.Width > 0, "the wider total crushed the heading out of the row");

        window.Close();
    }

    /// <summary>
    /// ET-90: NAME now floors at its own 180px <c>MinWidth</c>, not Avalonia's DataGrid-wide 20px — and the fixed
    /// QTY/PRICE EACH/TOTAL columns, which used to give way once NAME hit that lower floor (TOTAL 130 → 96 → 36,
    /// per ET-90-grooming), must never move again: the grid scrolls horizontally instead. Swept across the window's
    /// own minimum width (760, raised from 560 by this ticket) up to a generous 1100, with the ET-93 billion-ISK
    /// total as the widest realistic figure the money columns have to hold whole. An <c>ActualWidth &gt; 0</c>
    /// assertion would stay green on the pre-fix collapse; this checks the real floor. See
    /// [[domain/DataGrid-Star-Column-Collapse]].
    /// </summary>
    [AvaloniaTheory]
    [InlineData(760)]
    [InlineData(900)]
    [InlineData(1100)]
    public async Task NameColumn_KeepsItsMinimum_AndMoneyColumnsStayWhole(double windowWidth)
    {
        using var instance = _NewInstance(_Minerals());
        await _CachePricesAsync(instance, new DateTimeOffset(2026, 8, 31, 9, 30, 0, TimeSpan.Zero),
            (34, 1000), (35, 1400), (36, 1125));
        instance.Services.GetRequiredService<IThemeService>().Apply(FactionTheme.Gallente);

        var tool = _BuildTool(instance);
        tool.PasteText = MineralPaste;
        await tool.AppraiseCommand.ExecuteAsync(null);

        // 1,000,000 × 1,000 = 1,000,000,000 — the same figure ET-93 measured cutting off mid-digit at the old
        // 620px floor ("1,000,000,000 I").
        var tritanium = tool.Rows.Single(row => row.Name == "Tritanium");
        Assert.Equal("1,000,000,000 ISK", tritanium.TotalDisplay);

        var window = new AppraisalWindow(tool) { Width = windowWidth, Height = 640 };
        window.Show();
        await _WaitForAsync(() => false, tries: 12);

        window.CaptureRenderedFrame()!.Save(
            Path.Combine(_ShotDirectory(), $"eveutils-appraisal-namefloor-{windowWidth:0}.png"), new PngBitmapEncoderOptions());

        var grid = window.GetVisualDescendants().OfType<DataGrid>().Single();
        var byHeader = grid.Columns.ToDictionary(_HeaderText, column => column.ActualWidth);
        output.WriteLine($"window={windowWidth}: NAME={byHeader["NAME"]:0} QTY={byHeader["QTY"]:0} "
                          + $"PRICE EACH={byHeader["PRICE EACH"]:0} TOTAL={byHeader["TOTAL"]:0}");

        Assert.True(byHeader["NAME"] >= 180, $"NAME bottomed out at {byHeader["NAME"]:0}px, not its 180px minimum");
        Assert.Equal(90, byHeader["QTY"]);
        Assert.Equal(130, byHeader["PRICE EACH"]);
        Assert.Equal(130, byHeader["TOTAL"]);

        // Below the columns' combined minimum (180+90+130+130=530) the grid has to give way somewhere, and it must
        // be the scrollbar, not one of the columns just pinned above (ET-90's "prefer horizontal scroll" — confirmed
        // by hand against the renders saved above, where the DataGrid's own horizontal scrollbar sits at the bottom
        // of the grid's row area rather than snug under the last row).
        var hScroll = grid.GetVisualDescendants().OfType<ScrollBar>().Single(bar => bar.Orientation == Orientation.Horizontal);
        var shortfall = 180 + 90 + 130 + 130 - grid.Bounds.Width;
        if (shortfall > 0)
            Assert.True(hScroll.Maximum > 0, $"the grid was {shortfall:0}px short but its scrollbar reports Maximum={hScroll.Maximum}");
        else
            Assert.Equal(0, hScroll.Maximum);

        // Bounds, not text: a squeezed TextBlock keeps its full Text and renders an ellipsis rather than vanishing.
        // The DataGrid cell's own padding costs the TextBlock a fixed ~3px versus its DesiredSize even with the
        // column at its full pinned width and room to spare (confirmed identical at 760/900/1100, and against the
        // saved renders above, which show the figure whole) — a real squeeze is an order of magnitude larger, as
        // the pre-fix measurements were (TOTAL down to 36px against a >100px desired).
        var totalCell = grid.GetVisualDescendants().OfType<TextBlock>()
            .Single(block => block.Text == tritanium.TotalDisplay);
        Assert.True(totalCell.Bounds.Width >= totalCell.DesiredSize.Width - 5,
            $"the total was squeezed: {totalCell.Bounds.Width} arranged for {totalCell.DesiredSize.Width} desired");

        // Right-aligned so digit groups line up (ET-90): the figures' TextAlignment carries the visible alignment,
        // and the header TextBlock (an explicit column-header override, unlike NAME's plain string) sits at the
        // same right edge as the column's cells rather than trailing off on its own.
        Assert.Equal(TextAlignment.Right, totalCell.TextAlignment);
        var totalHeader = window.GetVisualDescendants().OfType<TextBlock>()
            .Single(block => block.Text == "TOTAL" && block.FindAncestorOfType<DataGridColumnHeader>() is not null);
        Assert.Equal(HorizontalAlignment.Right, totalHeader.HorizontalAlignment);

        // Bounds are relative to each control's own parent, not a shared origin, so the header and the cell — in
        // different branches of the DataGrid's template — need translating into one coordinate space before their
        // right edges mean anything next to each other.
        //
        // A residual ~12px gap survives even with the sort-icon reservation zeroed out below (DataGridColumnHeader
        // and DataGridCell just carry different built-in Fluent padding) — render-verified as visually flush, unlike
        // the ~20-32px gap the un-zeroed reservation left, which read as the header sitting off on its own.
        var headerRight = totalHeader.TranslatePoint(new Point(totalHeader.Bounds.Width, 0), window)!.Value.X;
        var cellRight = totalCell.TranslatePoint(new Point(totalCell.Bounds.Width, 0), window)!.Value.X;
        Assert.True(Math.Abs(headerRight - cellRight) < 15,
            $"TOTAL's header (right edge {headerRight}) does not line up with its cells (right edge {cellRight})");

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
        Assert.Contains("10,700,000 ISK", texts);
        Assert.Contains("Tritanium", texts);
        Assert.Contains("1,000,000", texts);
        // Present in the visual tree, not necessarily on screen: a TextBlock scrolled past the viewport keeps its
        // Text and stays IsEffectivelyVisible (the exact trap ET-90 exists to stop assuming past) — the grid check
        // below is what actually proves the docked host's width.
        Assert.Contains("5,000,000 ISK", texts);

        // ET-90: the docked host is narrower than the tool's own window ever gets (its MinWidth can't apply here),
        // so this is the one presentation path where the grid's minimum genuinely gets exercised. At the shipped
        // default main-window size (1100×720) it lands right at the columns' combined minimum, so — unlike the
        // pre-ET-90 layout, which fit all four columns by squeezing NAME to ~106px and the money columns not at
        // all yet — the grid now scrolls horizontally here too, even for ordinary figures, not just the
        // billion-ISK case. Confirmed against the render saved above and worth carrying into the write-up: this
        // is the direct, accepted cost of NAME's floor, not a defect.
        Assert.True(_NameColumnWidth(window) >= 180,
            $"NAME bottomed out at {_NameColumnWidth(window):0}px in the docked host, not its 180px minimum");
        var dockedGrid = window.GetVisualDescendants().OfType<DataGrid>().Single();
        var dockedShortfall = 180 + 90 + 130 + 130 - dockedGrid.Bounds.Width;
        var dockedHScroll = dockedGrid.GetVisualDescendants().OfType<ScrollBar>().Single(bar => bar.Orientation == Orientation.Horizontal);
        if (dockedShortfall > 0)
            Assert.True(dockedHScroll.Maximum > 0,
                $"the docked grid was {dockedShortfall:0}px short but its scrollbar reports Maximum={dockedHScroll.Maximum}");
        else
            Assert.Equal(0, dockedHScroll.Maximum);

        shell.ToggleDockModeCommand.Execute(null);   // → floating: the tool becomes its own window, not an orphan
        Dispatcher.UIThread.RunJobs();
        Assert.True(shell.IsHomeShown);

        shell.ToggleDockModeCommand.Execute(null);   // → docked again: the same module comes back as a tab
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("APPRAISAL", shell.HostTabs[0].Title);
        Assert.Same(tool, shell.SelectedHostTab!.Content.DataContext);   // and with what it had valued still in it

        // The dock→float→dock round trip reuses the same AppraisalWindow instance (ModuleHostService.Render just
        // steals its Content back), including the _isWide field _ApplyViewport tracks — so a stale read there would
        // survive the switch instead of showing up fresh. It does not: the column is still above its floor.
        Assert.True(_NameColumnWidth(window) >= 180,
            $"NAME bottomed out at {_NameColumnWidth(window):0}px after the dock/float round trip, not its 180px minimum");

        window.Close();
    }

    private static double _NameColumnWidth(Visual root) =>
        root.GetVisualDescendants().OfType<DataGrid>().Single().Columns
            .Single(column => _HeaderText(column) == "NAME").ActualWidth;

    /// <summary>QTY/PRICE EACH/TOTAL carry their header as a right-aligned <see cref="TextBlock"/> (ET-90), not the
    /// plain string NAME still uses, so a column's header text takes either form.</summary>
    private static string _HeaderText(DataGridColumn column) =>
        column.Header is TextBlock heading ? heading.Text! : column.Header!.ToString()!;

    /// <summary>The text actually on the screen — a hidden control is still in the visual tree, so a readout that is
    /// merely bound would pass an assertion that only looked at every <see cref="TextBlock"/>.</summary>
    private static TextBlock _Named(Visual root, string name) =>
        root.GetVisualDescendants().OfType<TextBlock>().Single(block => block.Name == name);

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
