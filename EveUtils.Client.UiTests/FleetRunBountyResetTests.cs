using System;
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
/// is keyed by <c>(FleetId, CharacterId)</c> and was never reset anywhere — despite <c>Sample()</c>'s own doc comment
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
}
