using Avalonia.Headless.XUnit;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Esi.Http;
using EveUtils.Shared.Modules.Market.Services;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>ET-65 phase 3: the running run's loot list with its in/out switch (AC-6) and its "why is there nothing
/// to show" state (AC-7). No window exists yet (ET-98 phase 4), so the ViewModel is driven directly here.</summary>
public sealed class RunLootViewModelTests
{
    private static readonly DateTime StartedAtUtc = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>AC-6's own tegenproef: exclude, assert the total drops; re-include, assert it comes back.
    /// Exclusion is a flag, never a removal — the row stays listed throughout.</summary>
    [AvaloniaFact]
    public async Task ExcludingACapture_LowersTheTotal_AndReincludingReturnsIt()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        Guid runId = await _StartRunAsync(dispatcher);
        await _AddCaptureAsync(dispatcher, "AAA", typeId: 34);
        await _AddCaptureAsync(dispatcher, "BBB", typeId: 35);

        var viewModel = new RunLootViewModel(dispatcher, new _Prices((34, 100), (35, 250))) { RunId = runId };
        await viewModel.RefreshAsync(Token);

        Assert.Equal(2, viewModel.Captures.Count);
        Assert.Equal(350m, viewModel.TotalIsk);

        RunLootCaptureRowViewModel row = viewModel.Captures[0];
        Assert.True(await viewModel.ToggleExcludedAsync(row, Token));
        Assert.True(row.IsExcluded);
        Assert.Equal(250m, viewModel.TotalIsk); // dropped: the excluded row's price no longer counts
        Assert.Equal(2, viewModel.Captures.Count); // still listed — excluding never removes it

        Assert.True(await viewModel.ToggleExcludedAsync(row, Token));
        Assert.False(row.IsExcluded);
        Assert.Equal(350m, viewModel.TotalIsk); // back to both
    }

    /// <summary>
    /// A type the price source has nothing for stays priceless, never a silent 0. The reason moved with the source:
    /// it used to be "the copied window showed no ISK column", it is now "no market price is known for this type".
    /// </summary>
    [AvaloniaFact]
    public async Task ATypeWithoutAMarketPrice_NeverBecomesZero()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        Guid runId = await _StartRunAsync(dispatcher);
        await _AddCaptureAsync(dispatcher, "AAA", typeId: 34);

        var viewModel = new RunLootViewModel(dispatcher, new _Prices()) { RunId = runId };
        await viewModel.RefreshAsync(Token);

        Assert.Null(viewModel.TotalIsk);
        Assert.Equal("no price", viewModel.TotalIskDisplay);
        Assert.Equal(1, viewModel.EntriesWithoutPrice);
    }

    /// <summary>
    /// A market price is per unit, so a stack of five is worth five of them. The clipboard column it replaced was a
    /// line total, which is why this used to assert the opposite (Raymond, 2026-09-02).
    /// </summary>
    [AvaloniaFact]
    public async Task AStack_IsWorthItsQuantityTimesTheUnitPrice()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        Guid runId = await _StartRunAsync(dispatcher);
        await _AddCaptureAsync(dispatcher, "stack", typeId: 34, quantity: 5);

        var viewModel = new RunLootViewModel(dispatcher, new _Prices((34, 100))) { RunId = runId };
        await viewModel.RefreshAsync(Token);

        Assert.Equal(500m, viewModel.LootIsk);
        Assert.Equal(500m, viewModel.NetIsk);
    }

    [AvaloniaFact]
    public async Task LostEntries_ReduceNetWithoutChangingLoot()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        Guid runId = await _StartRunAsync(dispatcher);
        await _AddCaptureAsync(dispatcher, "loot", typeId: 34);
        await _AddCaptureAsync(dispatcher, "filaments", typeId: 35, lootKind: LootKind.Lost, quantity: 3);

        var viewModel = new RunLootViewModel(dispatcher, new _Prices((34, 500), (35, 100))) { RunId = runId };
        await viewModel.RefreshAsync(Token);

        Assert.Equal(500m, viewModel.LootIsk);
        Assert.Equal(300m, viewModel.ConsumedIsk);
        Assert.Equal(200m, viewModel.NetIsk);
    }

    /// <summary>
    /// The counter-proof for the change itself: the clipboard's own ISK column is parsed and stored, and no figure
    /// on screen comes from it. Here it says 9 999 and the market says 100.
    /// </summary>
    [AvaloniaFact]
    public async Task TheClipboardIskColumn_IsNeverWhatTheRunIsValuedAt()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        Guid runId = await _StartRunAsync(dispatcher);
        await _AddCaptureAsync(dispatcher, "priced", typeId: 34, clipboardPrice: 9_999m);
        await _AddCaptureAsync(dispatcher, "unpriced", typeId: 99, clipboardPrice: 5_000m);

        var viewModel = new RunLootViewModel(dispatcher, new _Prices((34, 100))) { RunId = runId };
        await viewModel.RefreshAsync(Token);

        Assert.Equal(100m, viewModel.LootIsk);
        Assert.Equal(100m, viewModel.NetIsk);
        Assert.Equal(1, viewModel.EntriesWithoutPrice);   // type 99 has no market price, whatever the copy said
    }

    /// <summary>An empty price cache says why there are no figures instead of showing a total of zero.</summary>
    [AvaloniaFact]
    public async Task AnEmptyPriceCache_SaysSo_RatherThanTotallingZero()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        Guid runId = await _StartRunAsync(dispatcher);
        await _AddCaptureAsync(dispatcher, "AAA", typeId: 34);

        var viewModel = new RunLootViewModel(dispatcher, new _NoPrices()) { RunId = runId };
        await viewModel.RefreshAsync(Token);

        Assert.Null(viewModel.TotalIsk);
        Assert.Contains("no market prices have been cached", viewModel.TotalIskLabel, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The label names what priced these figures. ET-65 AC-5 forbade that wording because the total WAS the copied
    /// column; Raymond replaced the basis on 2026-09-02, so the label has to stop claiming otherwise.
    /// </summary>
    [Fact]
    public void TheTotalsLabel_NamesTheValuationSource_AndNotTheClipboard()
    {
        var viewModel = new RunLootViewModel(new _UnusedDispatcher());

        Assert.Contains("price", viewModel.TotalIskLabel, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("clipboard", viewModel.TotalIskLabel, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Raymond, 2026-09-04: eleven runs stopped and never saved sat in his store, and the LOOT section showed
    /// "11 runs are running, so which one's loot to show is ambiguous" instead of his run's own loot. It asked the
    /// store which run was running rather than reading the run it was already on.
    ///
    /// Counter-proof, and it is the whole point of this test: the eleven are all left standing here. Route the
    /// section back through a which-run-is-running lookup and it goes ambiguous again on exactly this fixture;
    /// reading <see cref="RunLootViewModel.RunId"/> is what makes the eleven irrelevant rather than fatal.
    /// </summary>
    [AvaloniaFact]
    public async Task WithElevenRunsStoppedAndNeverSaved_TheSectionStillShowsItsOwnRunsLoot()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();

        // Ten of the pile, exactly as his store held them: stopped, never saved, never discarded — so still "open".
        for (var i = 0; i < 10; i++)
        {
            Guid abandoned = await _StartRunAsync(dispatcher);
            Assert.True((await dispatcher.Send(new SetRunStoppedCommand(abandoned, StartedAtUtc.AddMinutes(5)), Token))
                .IsSuccess);
        }

        // The eleventh is this window's own: loot copied while it ran, then STOP, with the save decision still
        // pending. That leaves nothing running at all, which is the state his store was actually in — and the one
        // where a which-run-is-running lookup has eleven equal answers and refuses to pick.
        Guid runId = await _StartRunAsync(dispatcher);
        await _AddCaptureAsync(dispatcher, "AAA", typeId: 34);
        Assert.True((await dispatcher.Send(new SetRunStoppedCommand(runId, StartedAtUtc.AddMinutes(5)), Token))
            .IsSuccess);

        // The counter-proof, run rather than reasoned: a which-run-is-running lookup over this very fixture IS
        // ambiguous. AddRunLootCaptureCommand still asks that question — it has no run id to go on — so its refusal
        // here is the state the LOOT section used to inherit.
        Result<RunLootCaptureSaveResult> guessed = await dispatcher.Send(new AddRunLootCaptureCommand(
            new RunLootCaptureInput
            {
                CapturedAtUtc = StartedAtUtc, Source = LootCaptureSource.Clipboard, ContentHash = "ZZZ",
                Entries = [new RunLootEntryInput
                    { ItemTypeId = 34, Name = "Item 34", Quantity = 1, LootKind = LootKind.Gained }]
            }), Token);
        Assert.False(guessed.IsSuccess);
        Assert.Contains("11 runs are running", guessed.Messages[0].Text); // his sentence, reproduced exactly

        var viewModel = new RunLootViewModel(dispatcher, new _Prices((34, 100))) { RunId = runId };
        await viewModel.RefreshAsync(Token);

        Assert.Null(viewModel.RunStatusMessage);   // not ambiguous here: the section was never asked to guess
        Assert.Single(viewModel.Captures);
        Assert.Equal(100m, viewModel.TotalIsk);
    }

    /// <summary>The number does what the clock used to: it tells two captures apart. It does it better on the case
    /// the clock was worst at — a repeat reads "identical to #1" in one go, where two timestamps have to be compared
    /// to each other. Numbering is the run's order and never the reading order's accident, so every later copy of
    /// the same window points back at the first one and not at the copy before it.</summary>
    [AvaloniaTheory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task IdenticalCaptures_AreNumberedInOrder_AndEachPointsBackAtTheFirst(int copies)
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        Guid runId = await _StartRunAsync(dispatcher);
        for (int copy = 0; copy < copies; copy++)
            await _AddCaptureAsync(dispatcher, "SAME", typeId: 34);

        var viewModel = new RunLootViewModel(dispatcher, new _Prices((34, 100))) { RunId = runId };
        await viewModel.RefreshAsync(Token);

        Assert.Equal(copies, viewModel.Captures.Count);
        Assert.Equal("#1", viewModel.Captures[0].NumberDisplay);
        Assert.Null(viewModel.Captures[0].RepeatOfDisplay);
        foreach (RunLootCaptureRowViewModel repeat in viewModel.Captures.Skip(1))
        {
            Assert.Equal("not added · identical to #1", repeat.RepeatOfDisplay);
            Assert.True(repeat.IsExcluded);
            Assert.True(repeat.CanReinclude);   // the one exclusion with a way back: it may really have been looted twice
        }

        Assert.Equal($"#{copies}", viewModel.Captures[^1].NumberDisplay);
        Assert.Equal(100m, viewModel.TotalIsk);   // the repeats add nothing, however many of them there are
    }

    /// <summary>AC-7: a window with no run of its own is a state the reader is told about, not an unexplained empty
    /// list. The section no longer asks the store which run is running, so it no longer inherits that question's
    /// answer either — it says what it knows about itself.</summary>
    [AvaloniaFact]
    public async Task WithoutARunOfItsOwn_TheStatusExplainsIt()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();

        var viewModel = new RunLootViewModel(dispatcher);
        await viewModel.RefreshAsync(Token);

        Assert.Empty(viewModel.Captures);
        Assert.Contains("No run yet", viewModel.RunStatusMessage);
    }

    /// <summary>AC-7's four states: an anchor present has nothing to explain, the other three each say why —
    /// location watch lost with a reason, no anchor yet (a restart), or the clipboard watch itself off. Removing
    /// any one branch collapses two of these onto the same outcome, which is exactly the "silent nothing" AC-7
    /// forbids.</summary>
    [Fact]
    public void ApplyLocationState_CoversAllFourStates()
    {
        var viewModel = new RunLootViewModel(new _UnusedDispatcher());

        viewModel.ApplyLocationState(abyssalAnchor: StartedAtUtc, locationUnavailableReason: null, clipboardWatching: true);
        Assert.Null(viewModel.LocationStatusMessage); // anchor present: the accepting state, nothing to explain

        viewModel.ApplyLocationState(abyssalAnchor: null, locationUnavailableReason: EsiErrorKind.ScopeMissing, clipboardWatching: true);
        Assert.Contains("no location access", viewModel.LocationStatusMessage);

        viewModel.ApplyLocationState(abyssalAnchor: null, locationUnavailableReason: null, clipboardWatching: true);
        var restartMessage = viewModel.LocationStatusMessage;
        Assert.False(string.IsNullOrWhiteSpace(restartMessage));

        viewModel.ApplyLocationState(abyssalAnchor: StartedAtUtc, locationUnavailableReason: null, clipboardWatching: false);
        Assert.False(string.IsNullOrWhiteSpace(viewModel.LocationStatusMessage));
        Assert.NotEqual(restartMessage, viewModel.LocationStatusMessage); // a different reason, not the same shrug
    }

    private static async Task<Guid> _StartRunAsync(IDispatcher dispatcher)
    {
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Abyssal, StartedAtUtc,
            1234, "Abyssal Deadspace", 30000142), Token);
        Assert.True(started.IsSuccess);
        return started.Value;
    }

    private static async Task _AddCaptureAsync(IDispatcher dispatcher, string contentHash, int typeId,
        LootKind lootKind = LootKind.Gained, long quantity = 1, decimal? clipboardPrice = null)
    {
        Result<RunLootCaptureSaveResult> added = await dispatcher.Send(new AddRunLootCaptureCommand(new RunLootCaptureInput
        {
            CapturedAtUtc = StartedAtUtc,
            Source = LootCaptureSource.Clipboard,
            ContentHash = contentHash,
            Entries = [new RunLootEntryInput
            {
                ItemTypeId = typeId, Name = $"Item {typeId}", Quantity = quantity,
                ClipboardPrice = clipboardPrice, LootKind = lootKind
            }]
        }), Token);
        Assert.True(added.IsSuccess);
    }

    /// <summary>A price source with one answer per type id; a type it does not name simply has no price.</summary>
    private sealed class _Prices(params (int TypeId, double Estimate)[] prices) : IAppraisalProvider
    {
        public string Id => "test";

        public string DisplayName => "Test prices";

        public Task<Result<AppraisalOutcome>> AppraiseAsync(IReadOnlyCollection<AppraisalLine> lines,
            CancellationToken cancellationToken = default)
        {
            List<AppraisalRow> rows = [.. lines.Select(line => new AppraisalRow(line,
                Array.Exists(prices, price => price.TypeId == line.TypeId)
                    ? new AppraisalPrice(Array.Find(prices, price => price.TypeId == line.TypeId).Estimate)
                    : null))];
            return Task.FromResult(Result<AppraisalOutcome>.Success(new AppraisalOutcome(rows, [], "Test basis.")));
        }
    }

    /// <summary>An empty cache: the provider refuses rather than returning a total of zero.</summary>
    private sealed class _NoPrices : IAppraisalProvider
    {
        public string Id => "empty";

        public string DisplayName => "Empty cache";

        public Task<Result<AppraisalOutcome>> AppraiseAsync(IReadOnlyCollection<AppraisalLine> lines,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<AppraisalOutcome>.Failure(new ResultMessage(MessageSeverity.Warning,
                MessageCodes.PriceCacheEmpty, "No market prices have been cached yet.", "test")));
    }

    /// <summary>Stands in where a test never actually dispatches — <see cref="RunLootViewModel.Labels_NeverMentionAValuationSource"/>-style
    /// tests exercise pure logic and would fail loudly (not silently) if a code path started using it.</summary>
    private sealed class _UnusedDispatcher : IDispatcher
    {
        public Task<TResult> Query<TResult>(IQuery<TResult> query, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Not expected to be called.");

        public Task Send(ICommand command, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Not expected to be called.");

        public Task<TResult> Send<TResult>(ICommand<TResult> command, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Not expected to be called.");
    }
}
