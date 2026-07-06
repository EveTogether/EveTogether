using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Dialogs;
using EveUtils.Client.ViewModels.FitBrowser;
using EveUtils.Client.Views;
using EveUtils.Shared.Modules.Fittings.Dtos;
using EveUtils.Shared.Modules.Market.Entities;
using EveUtils.Shared.Modules.Market.Repositories;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// Fit-browser basics: the pure browser/tab/detail view-models drive name-search, client-side paging
/// (10/25/50/100) and the slot-detail grouping; the window renders headless. No SDE/Dogma — slots come straight from
/// the ESI <c>flag</c>, and intentional gaps between modules are preserved.
/// </summary>
public class FitBrowserTests
{
    private static EsiFitting Fit(string name, int shipTypeId, params (int TypeId, string Flag, int Qty)[] items) =>
        new(0, name, "", shipTypeId, items.Select(i => new EsiFittingItem(i.TypeId, i.Flag, i.Qty)).ToList());

    private static FitRowViewModel Row(string name, int shipTypeId = 587) =>
        new(Fit(name, shipTypeId, (1, "HiSlot0", 1)), "Tester", FallbackNameResolver.Instance);

    /// <summary>Stub resolver: maps a couple of ids to names, everything else falls through to the fallback format.</summary>
    private sealed class StubNames : ISdeNameResolver
    {
        private readonly Dictionary<int, string> _names = new() { [627] = "Thorax", [2] = "125mm Railgun II", [8] = "Hobgoblin II" };
        public string TypeName(int typeId) => _names.TryGetValue(typeId, out var n) ? n : $"type {typeId}";
        public string? GroupName(int typeId) => typeId == 627 ? "Cruiser" : null; // Thorax → Cruiser; everything else unknown
    }

    /// <summary>Stub price cache: returns the average for any requested id it knows, omitting the rest (mirrors the real
    /// repository, which leaves missing ids absent).</summary>
    private sealed class StubPrices : IMarketPriceRepository
    {
        private readonly Dictionary<int, double> _prices;
        public StubPrices(Dictionary<int, double> prices) => _prices = prices;

        public Task ReplaceAllAsync(IReadOnlyCollection<LocalMarketPrice> prices, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyDictionary<int, double>> GetAveragePricesAsync(IReadOnlyCollection<int> typeIds, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<int, double>>(
                typeIds.Where(_prices.ContainsKey).ToDictionary(id => id, id => _prices[id]));

        public Task<int> CountAsync(CancellationToken cancellationToken = default) => Task.FromResult(_prices.Count);
    }

    [Fact]
    public void Paging_DefaultsTo25_AndSplitsRowsAcrossPages()
    {
        var rows = Enumerable.Range(1, 37).Select(i => Row($"Fit {i:00}"));
        var tab = new FitBrowserTabViewModel("Local", rows);

        Assert.Equal(25, tab.PageSize);
        Assert.Equal(37, tab.TotalCount);
        Assert.Equal(25, tab.PagedRows.Count);
        Assert.Equal(2, tab.PageCount);
        Assert.True(tab.CanNext);
        Assert.False(tab.CanPrev);

        tab.NextPageCommand.Execute(null);
        Assert.Equal(2, tab.CurrentPage);
        Assert.Equal(12, tab.PagedRows.Count);
        Assert.False(tab.CanNext);
        Assert.True(tab.CanPrev);
    }

    [Fact]
    public void PageSizeChange_ResetsToFirstPage_AndResizesPage()
    {
        var tab = new FitBrowserTabViewModel("Local", Enumerable.Range(1, 37).Select(i => Row($"Fit {i:00}")));
        tab.NextPageCommand.Execute(null); // move off page 1

        tab.PageSize = 10;

        Assert.Equal(1, tab.CurrentPage);
        Assert.Equal(10, tab.PagedRows.Count);
        Assert.Equal(4, tab.PageCount);
    }

    [Fact]
    public void Search_FiltersByName_CaseInsensitive_AndResetsPage()
    {
        var rows = new[] { Row("Thorax PVE"), Row("Thorax PVP"), Row("Rifter Tackle"), Row("Catalyst Gank") };
        var tab = new FitBrowserTabViewModel("Local", rows) { CurrentPage = 1 };

        tab.Search = "thorax";

        Assert.Equal(2, tab.FilteredCount);
        Assert.Equal(2, tab.PagedRows.Count);
        Assert.All(tab.PagedRows, r => Assert.Contains("Thorax", r.Name));
        Assert.Equal(1, tab.CurrentPage);
    }

    [Fact]
    public void Search_MatchesTags_NotOnlyName()
    {
        var pvp = new FitRowViewModel(Fit("Thorax Roam", 627, (1, "HiSlot0", 1)), "Tester",
            FallbackNameResolver.Instance, tags: "pvp, solo");
        var mining = new FitRowViewModel(Fit("Venture Belt", 32880, (1, "HiSlot0", 1)), "Tester",
            FallbackNameResolver.Instance, tags: "mining, isk");
        var tab = new FitBrowserTabViewModel("Local", new[] { pvp, mining });

        tab.Search = "mining";   // a tag on the second fit, present in neither fit's name

        Assert.Equal(1, tab.FilteredCount);
        Assert.Equal("Venture Belt", Assert.Single(tab.PagedRows).Name);
    }

    [Fact]
    public void SelectingRow_BuildsDetail_GroupsSlotsInEveOrder_AndPreservesGaps()
    {
        // High slots with a deliberate gap (HiSlot0 + HiSlot2, no HiSlot1) plus mid/low/rig/drone/cargo.
        var fit = Fit("Gap Thorax", 627,
            (3, "HiSlot2", 1), (2, "HiSlot0", 1),
            (4, "MedSlot0", 1),
            (5, "LoSlot0", 1), (6, "LoSlot1", 1),
            (7, "RigSlot0", 1),
            (8, "DroneBay", 5),
            (9, "Cargo", 100));
        var tab = new FitBrowserTabViewModel("Local", new[] { new FitRowViewModel(fit, "Tester", FallbackNameResolver.Instance) });

        tab.SelectedRow = tab.PagedRows[0];
        var detail = tab.Detail!;

        Assert.True(tab.HasDetail);
        Assert.Equal(
            new[] { FitSlotCategory.High, FitSlotCategory.Medium, FitSlotCategory.Low, FitSlotCategory.Rig, FitSlotCategory.Drone, FitSlotCategory.Cargo },
            detail.SlotGroups.Select(g => g.Category).ToArray());

        // High group keeps the gap: ordered by slot index → HiSlot0 (type 2) before HiSlot2 (type 3).
        var high = detail.SlotGroups.First(g => g.Category == FitSlotCategory.High);
        Assert.Equal(new[] { "HiSlot0", "HiSlot2" }, high.Items.Select(i => i.Flag).ToArray());
        Assert.Equal(new[] { 2, 3 }, high.Items.Select(i => i.TypeId).ToArray());

        // Drone bay sums stacked quantity; the grid's module count is fitted slots only (high 2 + mid 1 + low 2 + rig 1 = 6).
        Assert.Equal(5, detail.SlotGroups.First(g => g.Category == FitSlotCategory.Drone).Count);
        Assert.Equal(6, new FitRowViewModel(fit, "Tester", FallbackNameResolver.Instance).ModuleCount);
    }

    [Fact]
    public void Names_ResolveViaSdeResolver_WithFallbackForUnknownIds()
    {
        var fit = Fit("Drone Thorax", 627, (2, "HiSlot0", 1), (99, "MedSlot0", 1), (8, "DroneBay", 5));
        var names = new StubNames();

        var row = new FitRowViewModel(fit, "Tester", names);
        Assert.Equal("Thorax", row.ShipTypeLabel); // hull id 627 resolved

        var tab = new FitBrowserTabViewModel("Local", new[] { row }, names);
        tab.SelectedRow = tab.PagedRows[0];
        var detail = tab.Detail!;

        Assert.Equal("Thorax", detail.ShipTypeLabel);
        Assert.Equal("125mm Railgun II", detail.SlotGroups.First(g => g.Category == FitSlotCategory.High).Items[0].TypeLabel);
        Assert.Equal("Hobgoblin II", detail.SlotGroups.First(g => g.Category == FitSlotCategory.Drone).Items[0].TypeLabel);
        Assert.Equal("type 99", detail.SlotGroups.First(g => g.Category == FitSlotCategory.Medium).Items[0].TypeLabel); // unknown → fallback
    }

    [AvaloniaFact]
    public async Task FitBrowser_Renders_WithSelectedDetail()
    {
        var names = new StubNames();
        var prices = new StubPrices(new() { [627] = 12_000_000, [2] = 800_000, [4] = 1_500_000, [5] = 300_000, [8] = 250_000 });
        var rows = Enumerable.Range(1, 8).Select(i => new FitRowViewModel(
            Fit($"Thorax {i}", 627, (2, "HiSlot0", 1), (4, "MedSlot0", 1), (5, "LoSlot0", 1), (8, "DroneBay", 5)),
            "Tester", names, prices: prices)).ToList();
        foreach (var row in rows)
            await row.LoadPriceAsync();   // populate the Price avg. column for the render
        var tab = new FitBrowserTabViewModel("Local", rows, names);
        tab.SelectedRow = tab.PagedRows[0];
        var vm = new FitBrowserViewModel(new[] { tab });

        var window = new FitBrowserWindow(vm) { Width = 1040, Height = 660 };
        window.Show();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save("/tmp/eveutils-fit-browser.png");
    }

    [Fact]
    public async Task LocalRow_EditAndDelete_InvokeCallbacksWithItsId()
    {
        int? edited = null;
        int? deleted = null;
        var row = new FitRowViewModel(Fit("Hawk", 11993), "Tester", new StubNames(), localFitId: 42,
            onEditMetadata: id => { edited = id; return Task.CompletedTask; },
            onDelete: id => { deleted = id; return Task.CompletedTask; });

        Assert.True(row.CanManage);
        await ((IAsyncRelayCommand)row.EditMetadataCommand).ExecuteAsync(null);
        await ((IAsyncRelayCommand)row.DeleteCommand).ExecuteAsync(null);

        Assert.Equal(42, edited);
        Assert.Equal(42, deleted);
    }

    [Fact]
    public void ServerRow_WithoutLocalId_CannotManage()
    {
        var row = new FitRowViewModel(Fit("Hawk", 11993), "Sharer", new StubNames());

        Assert.False(row.CanManage);
        Assert.False(row.EditMetadataCommand.CanExecute(null));
        Assert.False(row.DeleteCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task FitBrowser_Renders_WithManageActions()
    {
        var names = new StubNames();
        var row = new FitRowViewModel(Fit("Hawk — PvP", 11993, (2, "HiSlot0", 1)), "Tester", names, localFitId: 7,
            onEditMetadata: _ => Task.CompletedTask, onDelete: _ => Task.CompletedTask);
        var tab = new FitBrowserTabViewModel("Local", new[] { row }, names);
        var vm = new FitBrowserViewModel(new[] { tab });

        var window = new FitBrowserWindow(vm) { Width = 1040, Height = 660 };
        window.Show();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save("/tmp/eveutils-fit-browser-manage.png");
    }

    [AvaloniaFact]
    public async Task FitMetadataDialog_Renders()
    {
        var dialog = new FitMetadataDialog(new FitMetadataDraft("Hawk — PvP", "Cheap brawler for roams", "pvp, cheap"))
            { Width = 520, Height = 380 };
        dialog.Show();
        var frame = dialog.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save("/tmp/eveutils-fit-metadata-dialog.png");
        dialog.Close();
    }

    /// <summary>a row exposes the per-rack module counts, the per-module tooltip lines (name-resolved) and
    /// the uploader; with no image provider nothing is fetched.</summary>
    [Fact]
    public void Row_CountsModulesPerRack_AndExposesUploader()
    {
        var fit = Fit("Brawler", 627,
            (2, "HiSlot0", 1), (2, "HiSlot1", 1),
            (8, "MedSlot0", 1),
            (627, "LoSlot0", 1), (627, "LoSlot1", 1), (627, "LoSlot2", 1));
        var row = new FitRowViewModel(fit, "Vaelor Kestrane", new StubNames());

        Assert.Equal(2, row.HighCount);
        Assert.Equal(1, row.MidCount);
        Assert.Equal(3, row.LowCount);
        Assert.Equal("Vaelor Kestrane", row.Uploader);
        Assert.Equal(2, row.HighModules.Count);
        Assert.Equal("125mm Railgun II", row.HighModules[0].Name);   // StubNames maps type id 2
        Assert.False(row.HasHullImage);                              // no image provider → nothing loaded
        Assert.Equal("Cruiser", row.HullClass);                      // hull-class label from the SDE group
        Assert.True(row.HasHullClass);
    }

    /// <summary>a row whose hull class the SDE can't resolve hides the class label rather than showing a blank.</summary>
    [Fact]
    public void Row_WithoutResolvableHullClass_HasNoClassLabel()
    {
        var row = new FitRowViewModel(Fit("Mystery", 999, (1, "HiSlot0", 1)), "Tester", new StubNames());
        Assert.Null(row.HullClass);
        Assert.False(row.HasHullClass);
    }

    /// <summary>the row's price is the cached average of the hull plus every item × quantity — the same
    /// estimate as the fit-detail header.</summary>
    [Fact]
    public async Task Row_SumsFitValue_FromHullPlusItemsTimesQuantity()
    {
        var fit = Fit("Priced Thorax", 627, (2, "HiSlot0", 2), (8, "DroneBay", 5));
        var prices = new StubPrices(new() { [627] = 10_000_000, [2] = 1_000_000, [8] = 500_000 });
        var row = new FitRowViewModel(fit, "Tester", new StubNames(), prices: prices);

        await row.LoadPriceAsync();

        // 10M hull + 2 × 1M modules + 5 × 0.5M drones = 14.5M
        Assert.Equal(14_500_000d, row.Price);
        Assert.Equal("14.5 M ISK", row.PriceLabel);
    }

    /// <summary>an unpopulated price cache leaves the row's value null → the column shows the placeholder.</summary>
    [Fact]
    public async Task Row_PriceStaysEmpty_WhenCacheIsEmpty()
    {
        var fit = Fit("Unpriced", 627, (2, "HiSlot0", 1));
        var row = new FitRowViewModel(fit, "Tester", new StubNames(), prices: new StubPrices(new()));

        await row.LoadPriceAsync();

        Assert.Null(row.Price);
        Assert.Equal("—", row.PriceLabel);
    }

    /// <summary>the import dropdown's ESF-link entry runs the wired callback (the dialog + decode live in the
    /// host), and the dropdown only shows when at least one import action is available.</summary>
    [Fact]
    public async Task ImportEsfLinkCommand_RunsWiredCallback_AndDrivesCanImport()
    {
        var calledEsf = false;
        var vm = new FitBrowserViewModel(new[] { new FitBrowserTabViewModel("Local", new List<FitRowViewModel>()) },
            importEsfLink: () => { calledEsf = true; return Task.CompletedTask; });

        Assert.True(vm.CanImport);
        await vm.ImportEsfLinkCommand.ExecuteAsync(null);
        Assert.True(calledEsf);

        var noImports = new FitBrowserViewModel(new[] { new FitBrowserTabViewModel("Local", new List<FitRowViewModel>()) });
        Assert.False(noImports.CanImport);
    }
}
