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

    // ── What the window shows ───────────────────────────────────────────────────────────────────────

    /// <summary>AC-3 on screen: the excluded pilot's amount is a zero somebody chose, and reads as one. A dash or a
    /// blank would be the same pixels as "we have no figure", which is the confusion the criterion names.</summary>
    [AvaloniaFact]
    public void ExcludedParticipant_ReadsAsAChosenZeroRatherThanAMissingFigure()
    {
        RunParticipantViewModel hauler = _Participant("Hauler Bob", isPayoutEligible: false);
        List<RunParticipantViewModel> participants =
            [_Participant("Alpha"), _Participant("Bravo"), _Participant("Charlie"), _Participant("Delta"), hauler];

        RunPayoutSplit.Apply(participants, totalIsk: 400m);

        Assert.Equal(0m, hauler.PayoutIsk);
        Assert.Contains("0 ISK", hauler.PayoutDisplay);
        Assert.Contains("excluded", hauler.PayoutDisplay, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("no figure", hauler.PayoutDisplay, StringComparison.OrdinalIgnoreCase);

        // The hauler still flew the site, and the four who share say so out loud.
        Assert.True(hauler.IsParticipant);
        Assert.Contains("flew the site", hauler.StandingText);
        Assert.Contains("no share", hauler.StandingText);
        Assert.All(participants.Where(p => p.IsPayoutEligible), p => Assert.Equal(100m, p.PayoutIsk));
    }

    /// <summary>A participant with nothing to divide yet reads as "no figure", never as a zero — the mirror image of
    /// the assertion above, and what stops the two states collapsing into one.</summary>
    [AvaloniaFact]
    public void IncludedParticipantWithoutATotal_ReadsAsNoFigureRatherThanZero()
    {
        RunParticipantViewModel pilot = _Participant("Alpha");

        RunPayoutSplit.Apply([pilot], totalIsk: null);

        Assert.Null(pilot.PayoutIsk);
        Assert.DoesNotContain("0 ISK", pilot.PayoutDisplay);
        Assert.Contains("no figure", pilot.PayoutDisplay, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Re-including a pilot puts them back in the split rather than leaving them on the chosen zero.</summary>
    [AvaloniaFact]
    public void ReIncludingAPilot_PutsThemBackInTheSplit()
    {
        RunParticipantViewModel hauler = _Participant("Hauler Bob", isPayoutEligible: false);
        List<RunParticipantViewModel> participants = [_Participant("Alpha"), hauler];
        RunPayoutSplit.Apply(participants, 400m);
        Assert.Equal(0m, hauler.PayoutIsk);

        hauler.IsPayoutEligible = true;
        RunPayoutSplit.Apply(participants, 400m);

        Assert.Equal(200m, hauler.PayoutIsk);
        Assert.Equal(200m, participants[0].PayoutIsk);
    }

    // ── What the window may not claim ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The figure is an expectation, not a measurement. EVE pays every pilot who actually interacted, so an excluded
    /// hauler who fires one shot is paid by EVE regardless of our bookkeeping — the label has to say so rather than
    /// let the number imply otherwise.
    /// </summary>
    [AvaloniaFact]
    public void ThePayoutLabel_CallsItselfAnExpectationAndNamesEvesOwnRule()
    {
        string label = RunPayoutSplit.ExpectationLabel;

        Assert.Contains("not a measurement", label, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("wallet journal", label, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1000 damage", label, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("twice the ideal fleet size", label, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("bookkeeping", label, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Words that would turn the expectation into a claim about what actually arrived. "Received" is not on
    /// this list on purpose: the label uses it to point at the wallet journal, which is the disclaimer rather than
    /// the claim.</summary>
    [AvaloniaTheory]
    [InlineData("guaranteed")]
    [InlineData("actual payout")]
    [InlineData("will be paid")]
    [InlineData("you earned")]
    public void ThePayoutLabel_NeverClaimsTheFigureWasMeasured(string forbidden) =>
        Assert.DoesNotContain(forbidden, RunPayoutSplit.ExpectationLabel, StringComparison.OrdinalIgnoreCase);

    private static RunParticipantViewModel _Participant(string name, bool isPayoutEligible = true) =>
        new(Guid.CreateVersion7(), name.GetHashCode(StringComparison.Ordinal), name,
            isParticipant: true, isPayoutEligible);
}
