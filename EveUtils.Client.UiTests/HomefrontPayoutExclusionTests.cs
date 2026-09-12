using Avalonia.Headless.XUnit;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-105 AC-3 and the two-flags rule. The case that forces them apart: five characters run the site while a sixth
/// fetches ore. The hauler takes no ISK and still registers their loot — so "did not fly it" and "flew it unpaid"
/// have to stay two separate facts.
/// </summary>
public sealed class HomefrontPayoutExclusionTests
{
    private static readonly DateTime StartedAtUtc = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The counter-proof: exclude the hauler, and their loot is still on the run, they still count as a participant,
    /// and only the payout flag moved.
    /// </summary>
    [AvaloniaFact]
    public async Task ExcludingTheHauler_KeepsTheirLootAndTheirParticipation()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Result<Guid> started = await dispatcher.Send(new StartRunCommand(90000009, ActivityKind.Site, StartedAtUtc,
            1234, "Homefront", 30000142, GroupCode: "HF-H4U1"), cancellationToken);
        Guid haulerRun = started.Value;

        await dispatcher.Send(new SaveRunCommand(haulerRun, StartedAtUtc.AddMinutes(12), StartedAtUtc.AddMinutes(13),
            [
                new RunLootCaptureInput
                {
                    CapturedAtUtc = StartedAtUtc.AddMinutes(8),
                    Source = LootCaptureSource.Clipboard,
                    Entries =
                    [
                        new RunLootEntryInput { ItemTypeId = 1230, Name = "Veldspar", Quantity = 5000, Volume = 500m, ClipboardPrice = 4200m, LootKind = LootKind.Gained }
                    ]
                }
            ], [], [], []), cancellationToken);

        Result excluded = await dispatcher.Send(new SetRunPayoutEligibilityCommand(haulerRun, false), cancellationToken);

        Assert.True(excluded.IsSuccess);
        await using ClientDbContext db = await instance.Services
            .GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(cancellationToken);
        Run run = await db.Set<Run>().Include(candidate => candidate.LootCaptures)
            .ThenInclude(capture => capture.Entries)
            .SingleAsync(candidate => candidate.Id == haulerRun, cancellationToken);

        Assert.False(run.IsPayoutEligible);
        Assert.True(run.IsParticipant);            // the two flags did not move together
        Assert.Null(run.DeletedAtUtc);
        RunLootEntry entry = Assert.Single(Assert.Single(run.LootCaptures).Entries);
        Assert.Equal(4200m, entry.ClipboardPrice);
        Assert.Equal(5000, entry.Quantity);
    }

    /// <summary>A run started for a pilot who is not flying the site is a different fact again, and survives the
    /// round trip on its own. Without this, <c>IsParticipant</c> could be a constant and nothing would notice.</summary>
    [AvaloniaFact]
    public async Task ParticipationAndPayoutEligibility_AreStoredIndependently()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Result<Guid> flewUnpaid = await dispatcher.Send(new StartRunCommand(90000010, ActivityKind.Site, StartedAtUtc,
            1234, "Homefront", 30000142, IsParticipant: true, IsPayoutEligible: false), cancellationToken);
        Result<Guid> didNotFly = await dispatcher.Send(new StartRunCommand(90000011, ActivityKind.Site, StartedAtUtc,
            1234, "Homefront", 30000142, IsParticipant: false, IsPayoutEligible: true), cancellationToken);

        await using ClientDbContext db = await instance.Services
            .GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(cancellationToken);
        Run unpaid = await db.Set<Run>().SingleAsync(run => run.Id == flewUnpaid.Value, cancellationToken);
        Run absent = await db.Set<Run>().SingleAsync(run => run.Id == didNotFly.Value, cancellationToken);

        Assert.True(unpaid.IsParticipant);
        Assert.False(unpaid.IsPayoutEligible);
        Assert.False(absent.IsParticipant);
        Assert.True(absent.IsPayoutEligible);
    }

    // ── What FLEET shows (ET-272) ───────────────────────────────────────────────────────────────────

    /// <summary>The loot split is an exception, not a column: with everybody sharing there is nothing to say; the
    /// hauler left out reads as such, and the others' part is worked out on the spot — never "no figure yet".
    /// Counter-proof: count the left-out row among the sharers and each part drops to 80.</summary>
    [Fact]
    public void LeavingTheHaulerOutOfTheLootSplit_RecomputesTheOthersPart()
    {
        FleetCharacterRowViewModel[] rows = [_Row(1), _Row(2), _Row(3), _Row(4), _Row(5)];

        Assert.Null(FleetCharacterRowViewModel.LootSplitText(rows, 400m));

        rows[4].IsSharing = false;

        Assert.True(rows[4].IsLeftOutOfSplit);
        Assert.Equal("Loot split: 100 ISK each · 4 sharing, 1 left out", FleetCharacterRowViewModel.LootSplitText(rows, 400m));
        Assert.Equal("Loot split: no loot to split yet · 4 sharing, 1 left out", FleetCharacterRowViewModel.LootSplitText(rows, null));
    }

    private static FleetCharacterRowViewModel _Row(long characterId) => new(characterId) { IsLocal = true, CanToggleShare = true };
}
