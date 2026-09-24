using Avalonia.Headless.XUnit;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Market.Entities;
using EveUtils.Shared.Modules.Market.Repositories;
using EveUtils.Shared.Modules.Market.Services;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Sde;
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
        Assert.Equal("difference #2 → #3", section.DifferenceText);
        Assert.Null(section.LootIsk);            // 40 → 30 is a loss, so nothing was gained
        Assert.Equal(1_000m, section.ConsumedIsk);
    }

    /// <summary>Saving is the lock, and it is the commands that hold it rather than the window hiding its buttons —
    /// ET-179 saves a run left standing without any window being open at all. Counter-proof: drop either guard and
    /// one of these two comes back successful.</summary>
    [AvaloniaFact]
    public async Task OnceTheRunIsSaved_NeitherAHoldNorARoleCanBeChanged()
    {
        using var instance = _Instance();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        var section = await _SectionAsync(instance);
        section.CargoBeforeText = "Tritanium\t10";
        await section.LastCargoWrite;
        Guid captureId = section.Captures[0].CaptureId;

        Assert.True((await dispatcher.Send(new SaveRunCommand(section.RunId!.Value, StartedAtUtc,
            StartedAtUtc.AddMinutes(20), [], [], [], []), Token)).IsSuccess);

        Result<Guid> hold = await dispatcher.Send(new SetRunCargoHoldCommand(section.RunId!.Value,
            LootCaptureRole.CargoAfter, StartedAtUtc.AddMinutes(21),
            [new RunLootEntryInput { ItemTypeId = 34, Name = "Tritanium", Quantity = 99, LootKind = LootKind.Gained }]), Token);
        Result role = await dispatcher.Send(new SetRunLootCaptureRoleCommand(captureId, LootCaptureRole.CargoAfter), Token);

        Assert.False(hold.IsSuccess);
        Assert.False(role.IsSuccess);
        await section.RefreshAsync(Token);
        Assert.Equal(LootCaptureRole.CargoBefore, Assert.Single(section.Captures).Role);
    }

    /// <summary>ET-384 AC1 + AC3, pasted as EVE actually copies an inventory window: group, price, name, volume and
    /// quantity columns, an unchanged stack of paste and an unchanged single item beside a freshly looted item that
    /// arrived as two split stacks in the ending hold. Fixture captured in the shape measured in docs/clipboard.md
    /// (group first, name third — <c>ClipboardCaptureParserTests.ParseInventory_ReorderedColumnsAndBothLocales_MapsAnchoredValues</c>
    /// is the same shape).</summary>
    private const string BeforeHoldText =
        "Charges\t123,45 ISK\tNanite Repair Paste\t0,01 m3\t100\t\r\n"
        + "Salvage Materials\t50.000,00 ISK\tTripped Power Circuit\t0,10 m3\t1\t";

    private const string AfterHoldText =
        "Charges\t123,45 ISK\tNanite Repair Paste\t0,01 m3\t100\t\r\n"
        + "Salvage Materials\t50.000,00 ISK\tTripped Power Circuit\t0,10 m3\t1\t\r\n"
        + "Mutaplasmids\t10.000,00 ISK\tGravid Mutaplasmid\t0,01 m3\t2\t\r\n"
        + "Mutaplasmids\t10.000,00 ISK\tGravid Mutaplasmid\t0,01 m3\t3\t";

    [AvaloniaFact]
    public async Task PastingRealisticBeforeAndAfterText_CountsOnlyWhatChanged()
    {
        using var instance = TestClientInstance.Create(services => services.AddSingleton<ISdeAccessor>(new FakeSdeAccessor()
            .Add(28668, "Nanite Repair Paste", 285, 7)
            .Add(52, "Tripped Power Circuit", 448, 25)
            .Add(60, "Gravid Mutaplasmid", 1945, 35)));
        // _SectionAsync replaces the whole price table with its own Tritanium fixture, so Gravid Mutaplasmid's price
        // has to be set after it, not before.
        var section = await _SectionAsync(instance);
        await instance.Services.GetRequiredService<IMarketPriceRepository>().ReplaceAllAsync(
            [new LocalMarketPrice { TypeId = 60, AveragePrice = 3_000_000, AdjustedPrice = 3_000_000, UpdatedAt = DateTimeOffset.UtcNow }]);

        section.CargoBeforeText = BeforeHoldText;
        await section.LastCargoWrite;
        section.CargoAfterText = AfterHoldText;
        await section.LastCargoWrite;

        ActivityLootLineViewModel line = Assert.Single(section.ItemRows, row => !row.IsExcluded);
        Assert.Equal(60, line.ItemTypeId);
        Assert.Equal(5L, line.Quantity);
        Assert.Equal(15_000_000m, section.LootIsk);
    }

    private static TestClientInstance _Instance() =>
        TestClientInstance.Create(services => services.AddSingleton<ISdeAccessor>(
            new FakeSdeAccessor().Add(34, "Tritanium", 18, 4)));

    private static async Task<RunLootViewModel> _SectionAsync(TestClientInstance instance)
    {
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        await instance.Services.GetRequiredService<IMarketPriceRepository>().ReplaceAllAsync(
            [new LocalMarketPrice { TypeId = 34, AveragePrice = 100, AdjustedPrice = 100, UpdatedAt = DateTimeOffset.UtcNow }]);
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
