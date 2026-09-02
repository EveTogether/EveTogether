using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Client.Esi;
using EveUtils.Client.Fleet;
using EveUtils.Client.Transport;
using EveUtils.Shared.Modules.Esi.Http;
using EveUtils.Shared.Modules.Fleet.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// self-report driver: for each coupled server-fleet the client is a NON-boss member of, it reports whether
/// the pilot is in the coupled in-game fleet (comparing the pilot's own char-fleet to the fleet's EsiFleetId). Boss
/// fleets are left to the boss-mirror; uncoupled fleets are skipped; a transient char-fleet read skips the character.
/// </summary>
public class EsiSelfReportServiceTests
{
    private const string Server = "https://srv:1";
    private const int Pilot = 200;
    private const int Boss = 100;
    private const long EsiFleetId = 999;

    private static FleetInfo Coupled(long fleetId, long? esiFleetId, int? boss) =>
        new(fleetId, "Doctrine", null, FleetVisibility.Public, FleetState.Active, Boss,
            null, null, System.DateTimeOffset.UtcNow, FleetActivation.Active, null, esiFleetId, boss);

    private static FleetMemberInfo Member(long id, int characterId) =>
        new(id, characterId, -1, -1, FleetRole.SquadMember, false);

    private static EsiSelfReportService Service(FakeEsiFleetClient esi, RecordingFleetTransportClient transport) =>
        new(new NullSessionStore(), transport, esi, new EsiAvailabilityState(), NullLogger<EsiSelfReportService>.Instance);

    [Fact]
    public async Task NonBossMemberInTheCoupledFleet_ReportsInFleetTrue()
    {
        var esi = new FakeEsiFleetClient { CharFleet = new EsiCharacterFleet { FleetId = EsiFleetId, FleetBossId = Boss } };
        var transport = new RecordingFleetTransportClient();
        transport.MyFleetsByServer[Server] = [Coupled(7, EsiFleetId, Boss)];
        transport.MembersByFleet[7] = [Member(55, Pilot)];

        await Service(esi, transport).ReportForCharacterAsync(Server, Pilot, TestContext.Current.CancellationToken);

        var report = Assert.Single(transport.ReportedInGameFleet);
        Assert.Equal((55L, true, Pilot), report);
    }

    [Fact]
    public async Task PilotInADifferentInGameFleet_ReportsInFleetFalse()
    {
        var esi = new FakeEsiFleetClient { CharFleet = new EsiCharacterFleet { FleetId = 12345, FleetBossId = Pilot } };
        var transport = new RecordingFleetTransportClient();
        transport.MyFleetsByServer[Server] = [Coupled(7, EsiFleetId, Boss)];
        transport.MembersByFleet[7] = [Member(55, Pilot)];

        await Service(esi, transport).ReportForCharacterAsync(Server, Pilot, TestContext.Current.CancellationToken);

        Assert.Equal((55L, false, Pilot), Assert.Single(transport.ReportedInGameFleet));
    }

    [Fact]
    public async Task WhenWeAreTheBoss_DoesNotSelfReport()
    {
        var esi = new FakeEsiFleetClient { CharFleet = new EsiCharacterFleet { FleetId = EsiFleetId, FleetBossId = Boss } };
        var transport = new RecordingFleetTransportClient();
        transport.MyFleetsByServer[Server] = [Coupled(7, EsiFleetId, boss: Boss)];
        transport.MembersByFleet[7] = [Member(55, Boss)];

        await Service(esi, transport).ReportForCharacterAsync(Server, Boss, TestContext.Current.CancellationToken);

        Assert.Empty(transport.ReportedInGameFleet); // the boss-side mirror covers our own fleet
    }

    [Fact]
    public async Task UncoupledFleet_IsSkipped()
    {
        var esi = new FakeEsiFleetClient { CharFleet = new EsiCharacterFleet { FleetId = EsiFleetId, FleetBossId = Boss } };
        var transport = new RecordingFleetTransportClient();
        transport.MyFleetsByServer[Server] = [Coupled(7, esiFleetId: null, boss: null)];
        transport.MembersByFleet[7] = [Member(55, Pilot)];

        await Service(esi, transport).ReportForCharacterAsync(Server, Pilot, TestContext.Current.CancellationToken);

        Assert.Empty(transport.ReportedInGameFleet);
    }

    [Fact]
    public async Task TransientCharFleetFailure_SkipsTheCharacter_WithoutReporting()
    {
        var esi = new FakeEsiFleetClient { Error = EsiError.Of(EsiErrorKind.Timeout, "timeout", 504) };
        var transport = new RecordingFleetTransportClient();
        transport.MyFleetsByServer[Server] = [Coupled(7, EsiFleetId, Boss)];
        transport.MembersByFleet[7] = [Member(55, Pilot)];

        await Service(esi, transport).ReportForCharacterAsync(Server, Pilot, TestContext.Current.CancellationToken);

        Assert.Empty(transport.ReportedInGameFleet); // a timeout must not wrongly retract presence
    }

    [Fact]
    public async Task NoCoupledNonBossFleet_SkipsTheCharFleetReadEntirely()
    {
        // After an uncouple (here: the only fleet is no longer coupled) there is nothing to report into, so the
        // /characters/{id}/fleet/ ESI read must be skipped — otherwise it 404-polls that endpoint forever.
        var esi = new FakeEsiFleetClient { CharFleet = new EsiCharacterFleet { FleetId = EsiFleetId, FleetBossId = Boss } };
        var transport = new RecordingFleetTransportClient();
        transport.MyFleetsByServer[Server] = [Coupled(7, esiFleetId: null, boss: null)];
        transport.MembersByFleet[7] = [Member(55, Pilot)];

        await Service(esi, transport).ReportForCharacterAsync(Server, Pilot, TestContext.Current.CancellationToken);

        Assert.Equal(0, esi.CharFleetReads); // the wasted char-fleet read is never made
        Assert.Empty(transport.ReportedInGameFleet);
    }

}
