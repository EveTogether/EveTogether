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

/// <summary>The input side of the cargo difference: the two paste boxes, who the starting hold is, and the lock.
/// </summary>
public sealed class RunCargoHoldTests
{
    private static readonly DateTime StartedAtUtc = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>The whole input side in one run of the hands: paste both holds, the loot is the difference; paste one
    /// of them again and it is a correction of the same hold rather than a second sighting of it; then take the paste
    /// boxes off screen and watch the figures not move. That last line is the decision itself — the setting says
    /// which controls you see and never what the run is worth, because the stored run is rebuilt without it.
    /// Counter-proof: hang the count on the setting and the last assertion falls back to the sum of both holds.</summary>
    [AvaloniaFact]
    public async Task PastingBothHolds_MakesTheLootTheDifference_AndTheSettingNeverMovesAFigure()
    {
        using var instance = _Instance();
        var section = await _SectionAsync(instance);

        section.CargoBeforeText = "Tritanium\t10";
        await section.LastCargoWrite;
        section.CargoAfterText = "Tritanium\t30";
        await section.LastCargoWrite;

        Assert.Equal(2, section.Captures.Count);
        Assert.Equal(LootCaptureSource.Pasted, section.Captures[0].Source);
        Assert.Equal(2_000m, section.LootIsk);   // 20 more at 100, not the 40 that were pasted
        Assert.Equal("difference #1 → #2", section.DifferenceText);

        section.CargoAfterText = "Tritanium\t50";
        await section.LastCargoWrite;

        Assert.Equal(2, section.Captures.Count);   // rewritten, not a third row on the run
        Assert.Equal(4_000m, section.LootIsk);

        section.IsCargoDiffShown = false;
        await section.RefreshAsync(Token);
        Assert.Equal(4_000m, section.LootIsk);
        Assert.Equal("difference #1 → #2", section.DifferenceText);
    }

    /// <summary>One role, one holder — the reason two starting holds are impossible rather than caught. The capture
    /// that had it keeps its place and becomes an ordinary moment during the run: a correction may not make a cargo
    /// hold disappear.</summary>
    [AvaloniaFact]
    public async Task NamingAnotherCaptureTheStartingHold_TakesTheRoleOffTheOneThatHadIt()
    {
        using var instance = _Instance();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        var section = await _SectionAsync(instance);

        section.CargoBeforeText = "Tritanium\t10";
        await section.LastCargoWrite;
        // After the paste on the clock, because that is the order they happen in: you paste the hold you leave with,
        // then you copy what you pick up.
        await _CopyAsync(dispatcher, DateTime.UtcNow.AddMinutes(1), 40);
        await _CopyAsync(dispatcher, DateTime.UtcNow.AddMinutes(2), 30);
        await section.RefreshAsync(Token);

        Assert.True(await section.MakeCargoBeforeAsync(section.Captures[1], Token));

        Assert.Equal(3, section.Captures.Count);
        Assert.Equal(LootCaptureRole.Snapshot, section.Captures[0].Role);      // still listed, still says what it is
        Assert.Equal(LootCaptureRole.CargoBefore, section.Captures[1].Role);
        Assert.Equal("starting hold #2, no ending hold yet", section.DifferenceText);
    }

    /// <summary>ET-488, Jithran's Tetrimon Garrison run: starting hold pasted, a few cans of loot copied, then the
    /// ending hold. The copy of the cans used to stand in for the ending hold until one was named, and booked the whole
    /// starting hold as spent. A copy is listed as ignored and moves nothing, before the ending hold and after it.
    /// Counter-proof: give LootTally back its "last capture is the ending hold" and the first figures are a loss.</summary>
    [AvaloniaFact]
    public async Task WithAStartingHold_AClipboardCopyNeverMovesAFigure_AndIsListedAsIgnored()
    {
        using var instance = _Instance();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        var section = await _SectionAsync(instance);
        section.CargoBeforeText = "Tritanium\t10\nPyerite\t4";
        await section.LastCargoWrite;

        await _CopyAsync(dispatcher, DateTime.UtcNow.AddMinutes(1), 3);
        await section.RefreshAsync(Token);

        Assert.Null(section.LootIsk);
        Assert.Null(section.ConsumedIsk);
        Assert.Equal("starting hold #1, no ending hold yet", section.DifferenceText);
        Assert.True(section.Captures[1].IsIgnored);
        Assert.Equal("ignored — only the starting and ending hold count", section.Captures[1].StateText);

        section.CargoAfterText = "Tritanium\t30\nPyerite\t1";
        await section.LastCargoWrite;
        await _CopyAsync(dispatcher, DateTime.UtcNow.AddMinutes(3), 5);
        await section.RefreshAsync(Token);

        Assert.Equal(2_000m, section.LootIsk);
        Assert.Equal(150m, section.ConsumedIsk);
        Assert.Equal(2, section.IgnoredCount);
    }

    /// <summary>ET-488: with a starting hold the loot list is what came in and nothing else — what went down is its own
    /// CONSUMED list, per type with count, unit price and value adding up to CONSUMED, and a copy left out is not
    /// listed under the loot, since nothing between the holds counts anyway. Counter-proof: put the old single list
    /// back and the Pyerite and the struck-through copy come back under the Tritanium.</summary>
    [AvaloniaFact]
    public async Task WithAStartingHold_TheLootListIsWhatCameIn_AndWhatWentDownIsListedUnderConsumed()
    {
        using var instance = _Instance();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        var section = await _SectionAsync(instance);
        await _CopyAsync(dispatcher, DateTime.UtcNow.AddMinutes(-1), 10);
        await section.RefreshAsync(Token);
        Assert.True(await section.ToggleExcludedAsync(section.Captures[0], Token));
        section.CargoBeforeText = "Tritanium\t10\nPyerite\t4";
        await section.LastCargoWrite;
        section.CargoAfterText = "Tritanium\t30\nPyerite\t1";
        await section.LastCargoWrite;

        ActivityLootLineViewModel loot = Assert.Single(section.ItemRows);
        Assert.Equal((34, 20L, false, false), (loot.ItemTypeId, loot.Quantity, loot.IsLost, loot.IsExcluded));
        ActivityLootLineViewModel consumed = Assert.Single(section.ConsumedRows);
        Assert.Equal((35, 3L, 150m, "@ 50.00"), (consumed.ItemTypeId, consumed.Quantity, consumed.Value, consumed.UnitPriceText));
        Assert.Equal(section.ConsumedIsk, consumed.Value);
    }

    /// <summary>ET-488 addendum: a saved run counted between two holds is corrected by pasting a hold again — the same
    /// correction path as a rewritten list (revision, rebuilt totals). Which capture is the starting hold stays fixed.
    /// Counter-proof: give the hold command its save lock back and the correction is refused.</summary>
    [AvaloniaFact]
    public async Task OnceTheRunIsSaved_AHoldCanBeCorrected_ButTheStartingHoldStaysWhereItIs()
    {
        using var instance = _Instance();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        var section = await _SectionAsync(instance);
        section.CargoBeforeText = "Tritanium\t10";
        await section.LastCargoWrite;
        section.CargoAfterText = "Tritanium\t30";
        await section.LastCargoWrite;
        Guid runId = section.RunId!.Value;
        Assert.True((await dispatcher.Send(new SaveRunCommand(runId, StartedAtUtc,
            StartedAtUtc.AddMinutes(20), [], [], [], []), Token)).IsSuccess);
        Assert.True((await dispatcher.Send(new RebuildActivitySummariesCommand(), Token)).IsSuccess);
        int revision = (await _RunAsync(instance, runId)).Revision;
        section.IsLocked = true;
        Assert.True(section.CanOfferHoldEdit);
        Assert.False(section.CanOfferLootEdit);

        section.BeginHoldEditCommand.Execute(null);
        Assert.Equal("Tritanium\t30", section.EndingHoldEditor.Text);
        section.EndingHoldEditor.Text = "Tritanium\t50";
        Assert.True(await section.EndingHoldEditor.FinishAsync(Token));

        Assert.Equal(4_000m, section.LootIsk);
        Assert.Equal("difference #1 → #2", section.DifferenceText);
        Assert.Equal(revision + 1, (await _RunAsync(instance, runId)).Revision);
        await using (ClientDbContext db = await _ContextAsync(instance))
            Assert.Equal(4_000m, Assert.Single(await db.Set<ActivitySummary>().ToListAsync(Token)).LootIskNet);
        Result role = await dispatcher.Send(new SetRunLootCaptureRoleCommand(section.Captures[1].CaptureId,
            LootCaptureRole.CargoBefore), Token);
        Assert.False(role.IsSuccess);
    }

    private static TestClientInstance _Instance() =>
        TestClientInstance.Create(services => services.AddSingleton<ISdeAccessor>(
            new FakeSdeAccessor().Add(34, "Tritanium", 18, 4).Add(35, "Pyerite", 18, 4)));

    private static Task<ClientDbContext> _ContextAsync(TestClientInstance instance) =>
        instance.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(Token);

    private static async Task<Run> _RunAsync(TestClientInstance instance, Guid runId)
    {
        await using ClientDbContext db = await _ContextAsync(instance);
        return await db.Set<Run>().AsNoTracking().SingleAsync(run => run.Id == runId, Token);
    }

    private static async Task<RunLootViewModel> _SectionAsync(TestClientInstance instance)
    {
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        await instance.Services.GetRequiredService<IMarketPriceRepository>().ReplaceAllAsync(
        [
            new LocalMarketPrice { TypeId = 34, AveragePrice = 100, AdjustedPrice = 100, UpdatedAt = DateTimeOffset.UtcNow },
            new LocalMarketPrice { TypeId = 35, AveragePrice = 50, AdjustedPrice = 50, UpdatedAt = DateTimeOffset.UtcNow }
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

    private static async Task _CopyAsync(IDispatcher dispatcher, DateTime capturedAtUtc, long quantity)
    {
        Result<RunLootCaptureSaveResult> added = await dispatcher.Send(new AddRunLootCaptureCommand(new RunLootCaptureInput
        {
            CapturedAtUtc = capturedAtUtc,
            Source = LootCaptureSource.Clipboard,
            ContentHash = $"HASH-{quantity}",
            Entries = [new RunLootEntryInput { ItemTypeId = 34, Name = "Tritanium", Quantity = quantity, LootKind = LootKind.Gained }]
        }), Token);
        Assert.True(added.IsSuccess);
    }
}
