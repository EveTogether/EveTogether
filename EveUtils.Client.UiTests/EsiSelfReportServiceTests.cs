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

    /// <summary>Minimal <see cref="IEsiFleetClient"/> serving a configurable char-fleet; all write methods are unused.</summary>
    private sealed class FakeEsiFleetClient : IEsiFleetClient
    {
        public EsiCharacterFleet? CharFleet { get; init; }
        public EsiError? Error { get; init; }

        /// <summary>Counts the wasted /characters/{id}/fleet/ reads so a test can assert the gate skips them.</summary>
        public int CharFleetReads { get; private set; }

        public Task<EsiResult<EsiCharacterFleet>> GetCharacterFleetAsync(int characterId, CancellationToken cancellationToken = default)
        {
            CharFleetReads++;
            return Task.FromResult(Error is { } e ? EsiResult<EsiCharacterFleet>.Fail(e)
                : CharFleet is { } f ? EsiResult<EsiCharacterFleet>.Ok(f)
                : EsiResult<EsiCharacterFleet>.Fail(EsiError.Of(EsiErrorKind.NotFound, "not in a fleet", 404)));
        }

        public Task<EsiResult<EsiFleetMember[]>> GetMembersAsync(long fleetId, int actingCharacterId, CancellationToken cancellationToken = default) =>
            Task.FromResult(EsiResult<EsiFleetMember[]>.Ok([]));
        public Task<EsiResult<EsiFleetWing[]>> GetWingsAsync(long fleetId, int actingCharacterId, CancellationToken cancellationToken = default) =>
            Task.FromResult(EsiResult<EsiFleetWing[]>.Ok([]));
        public Task<EsiResult> SetFleetSettingsAsync(long fleetId, int actingCharacterId, string? motd, bool? isFreeMove, CancellationToken cancellationToken = default) =>
            Task.FromResult(EsiResult.Ok());
        public Task<EsiResult> MoveMemberAsync(long fleetId, int memberCharacterId, string role, long? wingId, long? squadId, int actingCharacterId, CancellationToken cancellationToken = default) =>
            Task.FromResult(EsiResult.Ok());
        public Task<EsiResult> KickMemberAsync(long fleetId, int memberCharacterId, int actingCharacterId, CancellationToken cancellationToken = default) =>
            Task.FromResult(EsiResult.Ok());
        public Task<EsiResult<long>> CreateWingAsync(long fleetId, int actingCharacterId, CancellationToken cancellationToken = default) =>
            Task.FromResult(EsiResult<long>.Ok(0));
        public Task<EsiResult> RenameWingAsync(long fleetId, long wingId, string name, int actingCharacterId, CancellationToken cancellationToken = default) =>
            Task.FromResult(EsiResult.Ok());
        public Task<EsiResult<long>> CreateSquadAsync(long fleetId, long wingId, int actingCharacterId, CancellationToken cancellationToken = default) =>
            Task.FromResult(EsiResult<long>.Ok(0));
        public Task<EsiResult> RenameSquadAsync(long fleetId, long squadId, string name, int actingCharacterId, CancellationToken cancellationToken = default) =>
            Task.FromResult(EsiResult.Ok());
        public Task<EsiResult> DeleteWingAsync(long fleetId, long wingId, int actingCharacterId, CancellationToken cancellationToken = default) =>
            Task.FromResult(EsiResult.Ok());
        public Task<EsiResult> DeleteSquadAsync(long fleetId, long squadId, int actingCharacterId, CancellationToken cancellationToken = default) =>
            Task.FromResult(EsiResult.Ok());
        public Task<EsiResult> InviteMemberAsync(long fleetId, int characterId, string role, long? wingId, long? squadId, int actingCharacterId, CancellationToken cancellationToken = default) =>
            Task.FromResult(EsiResult.Ok());
    }
}
