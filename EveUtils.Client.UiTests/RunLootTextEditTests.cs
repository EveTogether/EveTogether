using Avalonia.Headless.XUnit;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Market.Entities;
using EveUtils.Shared.Modules.Market.Repositories;
using EveUtils.Shared.Modules.Market.Services;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Sde;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>Correcting the loot as text — the way in and out of the list now that the in/out switch per capture is
/// gone. The switch could only take a whole capture; these are the corrections it could never make.</summary>
public sealed class RunLootTextEditTests
{
    private static readonly DateTime StartedAtUtc = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>The two corrections the old <c>exclude</c> button had no answer for: dropping one row out of a
    /// capture you otherwise want, and halving a quantity that was half your own. Editing replaces the list rather
    /// than adding to it, and the captures it was written from stay listed, excluded, with their rows intact — that
    /// readable difference is the only record of what the correction did.</summary>
    [AvaloniaTheory]
    [InlineData("Tritanium\t10", 1_000)]                     // Pyerite was never loot
    [InlineData("Tritanium\t10\nPyerite\t2", 1_500)]          // 18 of the 20 Pyerite were already aboard
    public async Task EditingTheList_ReplacesIt_AndLeavesTheCapturesItCameFromReadable(string edited, int expectedIsk)
    {
        using var instance = _Instance();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        var section = await _SectionAsync(instance);
        await _CopyAsync(dispatcher, "AAA", 34, "Tritanium", 10, StartedAtUtc.AddMinutes(1));
        await _CopyAsync(dispatcher, "BBB", 35, "Pyerite", 20, StartedAtUtc.AddMinutes(2));
        await section.RefreshAsync(Token);
        Assert.Equal(6_000m, section.TotalIsk);

        section.LootText = edited;
        Assert.True(section.CanFinishLootEdit);
        Assert.True(await section.ReplaceLootWithTextAsync(Token));

        Assert.Equal(expectedIsk, section.TotalIsk);
        Assert.Equal(3, section.Captures.Count);   // one written list, not a fourth copy of the two it replaced
        Assert.Single(section.Captures, capture => capture.Source is LootCaptureSource.Manual);
        foreach (RunLootCaptureRowViewModel superseded in section.Captures.Where(capture => capture.Source is LootCaptureSource.Clipboard))
        {
            Assert.True(superseded.IsExcluded);
            Assert.NotEmpty(superseded.Entries);          // still readable underneath, never removed
            Assert.NotNull(superseded.SubtotalDisplay);   // and still says what it was worth on its own
        }

        // Editing again rewrites the same one row rather than stacking a second written list on the run.
        section.LootText = "Tritanium\t1";
        Assert.True(await section.ReplaceLootWithTextAsync(Token));
        Assert.Equal(3, section.Captures.Count);
        Assert.Equal(100m, section.TotalIsk);
    }

    /// <summary>A capture that arrives after the pilot has written his list wins, lands under it, and says so. Loot
    /// that came in and quietly did not count is the one mistake he would never spot; a row he has to delete again
    /// is one he can see. Counter-proof: have the edit win instead and the total stays at 1.000 with nothing on
    /// screen to say a capture was dropped.</summary>
    [AvaloniaFact]
    public async Task ACaptureArrivingAfterTheEdit_CountsAndSaysWhereItWent()
    {
        using var instance = _Instance();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        var section = await _SectionAsync(instance);
        await _CopyAsync(dispatcher, "AAA", 34, "Tritanium", 10, StartedAtUtc.AddMinutes(1));
        await section.RefreshAsync(Token);

        section.LootText = "Tritanium\t10";
        Assert.True(await section.ReplaceLootWithTextAsync(Token));
        Assert.Equal(1_000m, section.TotalIsk);

        await _CopyAsync(dispatcher, "CCC", 35, "Pyerite", 4, DateTime.UtcNow.AddMinutes(5));
        await section.RefreshAsync(Token);

        Assert.Equal(2_000m, section.TotalIsk);   // 1.000 written by hand + 4 Pyerite at 250
        RunLootCaptureRowViewModel arrived = Assert.Single(section.Captures, capture => capture.IsAddedAfterEdit);
        Assert.Equal("#3", arrived.NumberDisplay);
        Assert.Contains("#3", section.AddedAfterEditNote);
        Assert.NotNull(section.ManualListCaption);
    }

    /// <summary>The window and the stored run count the same way, which is why the edit is stored as captures and
    /// never as text: <c>RebuildActivitySummaries</c> reads the rows back without knowing a correction happened, and
    /// has to arrive at the figure the pilot was looking at. Counter-proof: leave the superseded captures included
    /// and the summary comes back at 6.000 against the window's 1.000.</summary>
    [AvaloniaFact]
    public async Task TheEditedTotal_IsWhatTheSavedRunKeeps()
    {
        using var instance = _Instance();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        var section = await _SectionAsync(instance);
        await _CopyAsync(dispatcher, "AAA", 34, "Tritanium", 10, StartedAtUtc.AddMinutes(1));
        await _CopyAsync(dispatcher, "BBB", 35, "Pyerite", 20, StartedAtUtc.AddMinutes(2));
        await section.RefreshAsync(Token);

        section.LootText = "Tritanium\t10";
        Assert.True(await section.ReplaceLootWithTextAsync(Token));

        Assert.True((await dispatcher.Send(new SaveRunCommand(section.RunId!.Value, StartedAtUtc,
            StartedAtUtc.AddMinutes(20), [], [], [], []), Token)).IsSuccess);
        Assert.True((await dispatcher.Send(new RebuildActivitySummariesCommand(), Token)).IsSuccess);

        await using ClientDbContext db = await instance.Services
            .GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(Token);
        ActivitySummary summary = Assert.Single(await db.Set<ActivitySummary>().ToListAsync(Token));
        Assert.Equal(section.LootIsk, summary.LootIskGained);
        Assert.Equal(section.NetIsk, summary.LootIskNet);
        Assert.Equal(1_000m, summary.LootIskNet);
    }

    /// <summary>Writing the list out belongs to the way that has no starting hold. With one, the list is the
    /// difference between two cargo holds — so the thing to correct is those two, in the boxes that are already
    /// there, and a hand-written list would be a third answer to a question that already has one.</summary>
    [AvaloniaFact]
    public async Task WithAStartingHold_TheListIsCorrectedThroughTheHoldsInsteadOfAsText()
    {
        using var instance = _Instance();
        var section = await _SectionAsync(instance);
        Assert.True(section.CanEditLoot);

        section.CargoBeforeText = "Tritanium\t10";
        await section.LastCargoWrite;
        section.CargoAfterText = "Tritanium\t30";
        await section.LastCargoWrite;

        Assert.False(section.CanEditLoot);
        Assert.Equal("difference #1 → #2", section.DifferenceText);
        Assert.Equal(2_000m, section.LootIsk);
    }

    private static TestClientInstance _Instance() =>
        TestClientInstance.Create(services => services.AddSingleton<ISdeAccessor>(new FakeSdeAccessor()
            .Add(34, "Tritanium", 18, 4)
            .Add(35, "Pyerite", 18, 4)));

    private static async Task<RunLootViewModel> _SectionAsync(TestClientInstance instance)
    {
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        await instance.Services.GetRequiredService<IMarketPriceRepository>().ReplaceAllAsync(
        [
            new LocalMarketPrice { TypeId = 34, AveragePrice = 100, AdjustedPrice = 100, UpdatedAt = DateTimeOffset.UtcNow },
            new LocalMarketPrice { TypeId = 35, AveragePrice = 250, AdjustedPrice = 250, UpdatedAt = DateTimeOffset.UtcNow }
        ]);
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Abyssal, StartedAtUtc,
            1234, "Abyssal Deadspace", 30000142), Token);
        Assert.True(started.IsSuccess);

        var section = new RunLootViewModel(dispatcher,
            instance.Services.GetRequiredService<IAppraisalProvider>(),
            instance.Services.GetRequiredService<ISdeAccessor>())
        {
            RunId = started.Value,
            IsCargoDiffShown = true
        };
        await section.RefreshAsync(Token);
        return section;
    }

    private static async Task _CopyAsync(IDispatcher dispatcher, string contentHash, int typeId, string name,
        long quantity, DateTime capturedAtUtc)
    {
        Result<RunLootCaptureSaveResult> added = await dispatcher.Send(new AddRunLootCaptureCommand(new RunLootCaptureInput
        {
            CapturedAtUtc = capturedAtUtc,
            Source = LootCaptureSource.Clipboard,
            ContentHash = contentHash,
            Entries = [new RunLootEntryInput { ItemTypeId = typeId, Name = name, Quantity = quantity, LootKind = LootKind.Gained }]
        }), Token);
        Assert.True(added.IsSuccess);
    }
}
