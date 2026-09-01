using Avalonia.Headless.XUnit;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Esi.Http;
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
        await _StartRunAsync(dispatcher);
        await _AddCaptureAsync(dispatcher, "AAA", 100m);
        await _AddCaptureAsync(dispatcher, "BBB", 250m);

        var viewModel = new RunLootViewModel(dispatcher);
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

    /// <summary>A row the clipboard column showed no price for stays priceless, never a silent 0 (AC-5).</summary>
    [AvaloniaFact]
    public async Task ARowWithoutAPrice_NeverBecomesZero()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        await _StartRunAsync(dispatcher);
        await _AddCaptureAsync(dispatcher, "AAA", price: null);

        var viewModel = new RunLootViewModel(dispatcher);
        await viewModel.RefreshAsync(Token);

        Assert.Null(viewModel.TotalIsk);
        Assert.Equal("no price", viewModel.TotalIskDisplay);
        Assert.Equal(1, viewModel.EntriesWithoutPrice);
    }

    /// <summary>AC-5's label check: none of the ViewModel's exposed labels may name a valuation source — this is
    /// the clipboard's own column, not an appraisal.</summary>
    [Fact]
    public void Labels_NeverMentionAValuationSource()
    {
        var viewModel = new RunLootViewModel(new _UnusedDispatcher());
        string[] labels = [viewModel.TotalIskLabel, viewModel.EntriesWithoutPriceLabel];
        string[] banned = ["Jita", "markt", "market", "waardering", "appraisal"];

        foreach (var label in labels)
            foreach (var word in banned)
                Assert.DoesNotContain(word, label, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>AC-7: no running run is a state the reader is told about, not an unexplained empty list.</summary>
    [AvaloniaFact]
    public async Task WithoutARunningRun_TheStatusExplainsIt()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();

        var viewModel = new RunLootViewModel(dispatcher);
        await viewModel.RefreshAsync(Token);

        Assert.Empty(viewModel.Captures);
        Assert.Contains("No run is running", viewModel.RunStatusMessage);
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

    private static async Task _StartRunAsync(IDispatcher dispatcher)
    {
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Abyssal, StartedAtUtc,
            1234, "Abyssal Deadspace", 30000142), Token);
        Assert.True(started.IsSuccess);
    }

    private static async Task _AddCaptureAsync(IDispatcher dispatcher, string contentHash, decimal? price)
    {
        Result<RunLootCaptureSaveResult> added = await dispatcher.Send(new AddRunLootCaptureCommand(new RunLootCaptureInput
        {
            CapturedAtUtc = StartedAtUtc,
            Source = LootCaptureSource.Clipboard,
            ContentHash = contentHash,
            Entries = [new RunLootEntryInput { ItemTypeId = 34, Name = "Tritanium", Quantity = 1, ClipboardPrice = price, LootKind = LootKind.Gained }]
        }), Token);
        Assert.True(added.IsSuccess);
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
