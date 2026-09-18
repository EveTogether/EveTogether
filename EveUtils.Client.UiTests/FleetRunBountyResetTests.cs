using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Fleet;
using EveUtils.Client.Gamelog;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Metrics;
using EveUtils.Shared.Modules.Gamelog.Models;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Events;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// Review finding on PR #253, 2026-09-09: Jithran ran a site with five of his own toons in one fleet. Four alts,
/// freshly joined that fleet, showed the site's true bounty share (286,875 ISK each). His own long-running toon
/// showed 5,286,875 — exactly 5,000,000 too much. Measured, not guessed: <c>GamelogClientService._fleetRunBounty</c>
/// was keyed by <c>(FleetId, CharacterId)</c> and was never reset anywhere — despite <c>Sample()</c>'s own doc comment
/// promising "only ISK earned since this character started participating in this fleet". His toon had been in that
/// fleet since an EARLIER run (an unrelated 5,000,000 ISK kill), so the SECOND run's meter inherited it; the four
/// alts had just joined and started clean. The bug is in the recording (this dictionary never resetting), not in the
/// aggregation (ActivityWindowViewModel._fleetIsk sums whatever it is handed, correctly).
/// </summary>
public class FleetRunBountyResetTests
{
    private const long FleetId = 7;
    private const int CharacterId = 90000123;

    /// <summary>
    /// Counter-proof, red against the pre-fix code: a second run in the same fleet must start its bounty meter at
    /// zero, not carry over an earlier run's payout. A test with only one run would stay green either way — the
    /// carry-over only shows up on the SECOND run, which is exactly what a single-run scenario never exercises
    /// (Jithran's own "bij single ging dit eerder wel goed").
    /// </summary>
    [AvaloniaFact]
    public async Task ASecondRunInTheSameFleet_StartsItsBountyMeterAtZero()
    {
        using var instance = TestClientInstance.Create();
        var gamelog = instance.Services.GetRequiredService<GamelogClientService>();
        var eventBus = instance.Services.GetRequiredService<IEventBus>();
        gamelog.MapCharacter(CharacterId, "Pilot");
        instance.Services.GetRequiredService<IFleetParticipation>()
            .Set([new FleetParticipant(CharacterId, FleetId, ClientOnly: true)]);

        // First run: 5,000,000 ISK earned — an earlier, unrelated site.
        await eventBus.PublishAsync(new RunStartedEvent(Guid.NewGuid(), CharacterId, ActivityKind.Site,
            DateTime.UtcNow, FleetId, "HF-FIRST", isFleetCommander: false));
        await gamelog.AddBountyAsync("Pilot", new BountyEvent(DateTime.UtcNow, 5_000_000));

        // Second run, same fleet, same character — this is the one Jithran was actually looking at.
        await eventBus.PublishAsync(new RunStartedEvent(Guid.NewGuid(), CharacterId, ActivityKind.Site,
            DateTime.UtcNow, FleetId, "HF-SECOND", isFleetCommander: false));
        await gamelog.AddBountyAsync("Pilot", new BountyEvent(DateTime.UtcNow, 286_875));

        double bounty = gamelog.Sample(FleetId, CharacterId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            .First(sample => sample.Kind == MetricKind.Bounty).Value;

        Assert.Equal(286_875, bounty);
    }

    /// <summary>
    /// The multi-character shape of Jithran's own report: one character with an earlier run's bounty still on the
    /// books, another that only just joined the fleet. Each must show only what THEIR OWN current run earned — the
    /// sum across the group must equal what was actually paid out in the run being looked at, and neither character
    /// may carry the other's (or their own past run's) ISK.
    /// </summary>
    [AvaloniaFact]
    public async Task TwoCharactersInOneFleet_EachShowsOnlyTheCurrentRunsOwnBounty()
    {
        const int veteran = 90000123;   // was already in the fleet for an earlier run
        const int freshAlt = 90000124;  // just joined for this run

        using var instance = TestClientInstance.Create();
        var gamelog = instance.Services.GetRequiredService<GamelogClientService>();
        var eventBus = instance.Services.GetRequiredService<IEventBus>();
        gamelog.MapCharacter(veteran, "Veteran");
        gamelog.MapCharacter(freshAlt, "Fresh Alt");

        // The veteran's earlier, unrelated run in the same fleet.
        instance.Services.GetRequiredService<IFleetParticipation>()
            .Set([new FleetParticipant(veteran, FleetId, ClientOnly: true)]);
        await eventBus.PublishAsync(new RunStartedEvent(Guid.NewGuid(), veteran, ActivityKind.Site,
            DateTime.UtcNow, FleetId, "HF-FIRST", isFleetCommander: false));
        await gamelog.AddBountyAsync("Veteran", new BountyEvent(DateTime.UtcNow, 5_000_000));

        // Now the fresh alt joins too, and this run starts for both.
        instance.Services.GetRequiredService<IFleetParticipation>()
            .Set([new FleetParticipant(veteran, FleetId, ClientOnly: true),
                  new FleetParticipant(freshAlt, FleetId, ClientOnly: true)]);
        await eventBus.PublishAsync(new RunStartedEvent(Guid.NewGuid(), veteran, ActivityKind.Site,
            DateTime.UtcNow, FleetId, "HF-SECOND", isFleetCommander: false));
        await eventBus.PublishAsync(new RunStartedEvent(Guid.NewGuid(), freshAlt, ActivityKind.Site,
            DateTime.UtcNow, FleetId, "HF-SECOND", isFleetCommander: false));

        // Four Sigils at 337,500 each, split two ways for this run: 675,000 apiece — the true payout this run made.
        await gamelog.AddBountyAsync("Veteran", new BountyEvent(DateTime.UtcNow, 675_000));
        await gamelog.AddBountyAsync("Fresh Alt", new BountyEvent(DateTime.UtcNow, 675_000));

        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        double veteranBounty = gamelog.Sample(FleetId, veteran, nowMs).First(s => s.Kind == MetricKind.Bounty).Value;
        double freshAltBounty = gamelog.Sample(FleetId, freshAlt, nowMs).First(s => s.Kind == MetricKind.Bounty).Value;

        Assert.Equal(675_000, veteranBounty);
        Assert.Equal(675_000, freshAltBounty);
        // The group's own sum must equal what was really paid out this run — 1,350,000 — not the 6,350,000 Jithran
        // actually saw (which included the veteran's stale 5,000,000 from a run that had nothing to do with this one).
        Assert.Equal(1_350_000, veteranBounty + freshAltBounty);
    }

    /// <summary>
    /// ET-309, HF-RKM8 as Jithran flew it on 2026-09-18: five own characters in one fleet since the day before, where
    /// each earned 67,500 in HF-KKCW. Today only two of them started the mission. Counter-proof, red against the code
    /// before this fix: the reset only ran for a character that STARTED a run, so the three who stayed docked still
    /// published yesterday's 67,500 as this run's bounty.
    /// </summary>
    [AvaloniaFact]
    public async Task FleetMembersWhoDidNotStartTheNewRun_PublishNoBountyFromTheirEarlierRun()
    {
        int[] five = [90000201, 90000202, 90000203, 90000204, 90000205];
        using var instance = TestClientInstance.Create();
        var gamelog = instance.Services.GetRequiredService<GamelogClientService>();
        var eventBus = instance.Services.GetRequiredService<IEventBus>();
        instance.Services.GetRequiredService<IFleetParticipation>()
            .Set([.. five.Select(id => new FleetParticipant(id, FleetId, ClientOnly: true))]);

        Dictionary<int, Guid> yesterday = [];
        foreach (int id in five)
        {
            gamelog.MapCharacter(id, $"Toon {id}");
            yesterday[id] = Guid.NewGuid();
            await eventBus.PublishAsync(new RunStartedEvent(yesterday[id], id, ActivityKind.Site, DateTime.UtcNow,
                FleetId, "HF-KKCW", isFleetCommander: false));
            await gamelog.AddBountyAsync($"Toon {id}", new BountyEvent(DateTime.UtcNow, 67_500));
            await eventBus.PublishAsync(new RunSavedEvent(yesterday[id]));
        }

        Guid jithranToday = Guid.NewGuid();
        Guid abnobaToday = Guid.NewGuid();
        await eventBus.PublishAsync(new RunStartedEvent(jithranToday, five[0], ActivityKind.Mission, DateTime.UtcNow,
            FleetId, "HF-RKM8", isFleetCommander: true));
        await eventBus.PublishAsync(new RunStartedEvent(abnobaToday, five[1], ActivityKind.Mission, DateTime.UtcNow,
            FleetId, "HF-RKM8", isFleetCommander: false));
        await gamelog.AddBountyAsync($"Toon {five[0]}", new BountyEvent(DateTime.UtcNow, 13_046_750));

        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Assert.Equal(13_046_750, BountyOf(gamelog, five[0], nowMs));
        Assert.Equal(0, BountyOf(gamelog, five[1], nowMs));
        foreach (int docked in five.Skip(2))
            Assert.Equal(0, BountyOf(gamelog, docked, nowMs));

        Assert.Equal(13_046_750, gamelog.GetRunBounty(jithranToday));
        Assert.Equal(0, gamelog.GetRunBounty(abnobaToday));
        // Yesterday's figures are still yesterday's runs' own — kept, and readable only under those runs.
        Assert.All(five, id => Assert.Equal(67_500, gamelog.GetRunBounty(yesterday[id])));
    }

    /// <summary>ET-309: a run's tally belongs to that run from its first payout — a new run is a new key, so it starts at
    /// zero without anything having to reset it, and the earlier run keeps its own figure.</summary>
    [AvaloniaFact]
    public async Task ANewRun_StartsAtZero_AndTheEarlierRunKeepsItsOwnTally()
    {
        using var instance = TestClientInstance.Create();
        var gamelog = instance.Services.GetRequiredService<GamelogClientService>();
        var eventBus = instance.Services.GetRequiredService<IEventBus>();
        gamelog.MapCharacter(CharacterId, "Pilot");

        Guid first = Guid.NewGuid();
        await eventBus.PublishAsync(new RunStartedEvent(first, CharacterId, ActivityKind.Site, DateTime.UtcNow,
            FleetId, "HF-FIRST", isFleetCommander: false));
        await gamelog.AddBountyAsync("Pilot", new BountyEvent(DateTime.UtcNow, 400_000));

        Guid second = Guid.NewGuid();
        await eventBus.PublishAsync(new RunStartedEvent(second, CharacterId, ActivityKind.Site, DateTime.UtcNow,
            FleetId, "HF-SECOND", isFleetCommander: false));

        Assert.Equal(0, gamelog.GetRunBounty(second));
        Assert.Equal(0, BountyOf(gamelog, CharacterId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

        await gamelog.AddBountyAsync("Pilot", new BountyEvent(DateTime.UtcNow, 120_000));
        await gamelog.AddBountyAsync("Pilot", new BountyEvent(DateTime.UtcNow, 30_000));

        Assert.Equal(150_000, gamelog.GetRunBounty(second));
        Assert.Equal(400_000, gamelog.GetRunBounty(first));
        Assert.Equal(150_000, BountyOf(gamelog, CharacterId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
    }

    /// <summary>ET-309: once a run is saved its pilot is flying nothing, so what they earn next belongs to no run and the
    /// fleet meter reads zero — the saved run's own figure stays as it was.</summary>
    [AvaloniaFact]
    public async Task AfterSave_ThePilotPublishesZero_AndLaterPayoutsLandOnNoRun()
    {
        using var instance = TestClientInstance.Create();
        var gamelog = instance.Services.GetRequiredService<GamelogClientService>();
        var eventBus = instance.Services.GetRequiredService<IEventBus>();
        gamelog.MapCharacter(CharacterId, "Pilot");

        Guid run = Guid.NewGuid();
        await eventBus.PublishAsync(new RunStartedEvent(run, CharacterId, ActivityKind.Site, DateTime.UtcNow,
            FleetId, "HF-SAVED", isFleetCommander: false));
        await gamelog.AddBountyAsync("Pilot", new BountyEvent(DateTime.UtcNow, 250_000));
        await eventBus.PublishAsync(new RunSavedEvent(run));
        await gamelog.AddBountyAsync("Pilot", new BountyEvent(DateTime.UtcNow, 90_000));

        Assert.Equal(0, BountyOf(gamelog, CharacterId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        Assert.Equal(250_000, gamelog.GetRunBounty(run));
    }

    /// <summary>ET-309: a run is filed under one fleet. The same character's sample for another fleet has no run there.</summary>
    [AvaloniaFact]
    public async Task ARunInOneFleet_IsNotPublishedAsBountyInAnother()
    {
        using var instance = TestClientInstance.Create();
        var gamelog = instance.Services.GetRequiredService<GamelogClientService>();
        var eventBus = instance.Services.GetRequiredService<IEventBus>();
        gamelog.MapCharacter(CharacterId, "Pilot");

        await eventBus.PublishAsync(new RunStartedEvent(Guid.NewGuid(), CharacterId, ActivityKind.Site, DateTime.UtcNow,
            FleetId, "HF-HERE", isFleetCommander: false));
        await gamelog.AddBountyAsync("Pilot", new BountyEvent(DateTime.UtcNow, 75_000));

        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Assert.Equal(75_000, BountyOf(gamelog, CharacterId, nowMs));
        Assert.Equal(0, gamelog.Sample(FleetId + 1, CharacterId, nowMs).First(s => s.Kind == MetricKind.Bounty).Value);
    }

    private static double BountyOf(GamelogClientService gamelog, int characterId, long nowMs) =>
        gamelog.Sample(FleetId, characterId, nowMs).First(sample => sample.Kind == MetricKind.Bounty).Value;
}
