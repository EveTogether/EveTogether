using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Client.Fleet;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Enums;
using EveUtils.Shared.Modules.Fleet.Metrics;
using EveUtils.Shared.Modules.Fleet.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// The client-only half of the automatic stop (ET-167). A fleet that lives purely in this client is nobody else's to
/// stand down — no server sees it and no other client may touch it — and this client has no periodic fleet task, so
/// the reckoning happens when the app is opened again.
///
/// Modelled the way it actually happens: an instance is created, a fleet is started in it, the instance is disposed
/// keeping its database, and a second instance opens on that same data. That is a pilot closing EVE Together and
/// opening it later, and it is the only moment at which a local fleet's silence can be observed at all.
/// </summary>
public class LocalFleetAutoStopTests
{
    private const int Owner = 95001670;
    private const int Mate = 95001671;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>An instance with one started client-only fleet on it, owner already on the roster.</summary>
    private static async Task<(TestClientInstance Instance, long FleetId)> StartedLocalFleetAsync(
        params int[] extraMembers)
    {
        var instance = TestClientInstance.Create();
        var services = instance.Services;
        await services.GetRequiredService<ICharacterRegistry>()
            .AddOrUpdateAsync(new Character("Local FC", Owner), Ct);

        var fleets = services.GetRequiredService<ClientFleetService>();
        var repository = services.GetRequiredService<IFleetRepository>();

        var created = await fleets.CreateLocalFleetAsync("Wednesday Homefronts", null, Owner, cancellationToken: Ct);
        Assert.True(created.IsSuccess);
        var fleetId = created.Value;

        foreach (var characterId in extraMembers)
            await repository.AddMemberAsync(new FleetMember
            {
                FleetId = fleetId, CharacterId = characterId, Role = FleetRole.SquadMember, WingId = -1, SquadId = -1,
            }, Ct);

        Assert.True((await fleets.StartFleetAsync(fleetId, Owner, Ct)).IsSuccess);
        return (instance, fleetId);
    }

    private static async Task SeeAsync(IServiceProvider services, long fleetId, int characterId, DateTimeOffset at) =>
        await services.GetRequiredService<IFleetRepository>().TouchMemberSeenAsync(fleetId, characterId, at, Ct);

    private static async Task<FleetActivation> ActivationOf(IServiceProvider services, long fleetId) =>
        (await services.GetRequiredService<IFleetRepository>().GetAsync(fleetId, Ct))!.Activation;

    /// <summary>
    /// The acceptance criterion for the local half. The app was closed while a fleet was running; by the time it is
    /// opened again everyone has been quiet for far longer than the silence window, so the fleet stands down — with
    /// its roster still on it, ready to start again next Wednesday.
    /// </summary>
    [Fact]
    public async Task AFleetLeftRunningWhenTheAppWasClosed_StandsDownAtTheNextStart()
    {
        var (first, fleetId) = await StartedLocalFleetAsync(Mate);
        await SeeAsync(first.Services, fleetId, Owner, DateTimeOffset.UtcNow - TimeSpan.FromDays(2));
        await SeeAsync(first.Services, fleetId, Mate, DateTimeOffset.UtcNow - TimeSpan.FromDays(2));
        first.KeepDataOnDispose = true;
        var instanceName = first.InstanceName;
        first.Dispose();

        using var reopened = TestClientInstance.Create(instanceName: instanceName);
        var stopped = await reopened.Services.GetRequiredService<LocalFleetAutoStopService>()
            .ReconcileAsync(DateTimeOffset.UtcNow, Ct);

        Assert.Equal((fleetId, FleetStopTrigger.AllMembersOffline), Assert.Single(stopped));
        Assert.Equal(FleetActivation.Forming, await ActivationOf(reopened.Services, fleetId));
        Assert.Equal(2, (await reopened.Services.GetRequiredService<IFleetRepository>().ListMembersAsync(fleetId, Ct)).Count);
    }

    /// <summary>
    /// "Closing the app does not stop a fleet" still holds, and this is where it is at risk: a crash and an immediate
    /// restart must find the fleet exactly as it was. Everyone was heard from seconds ago, so there is no silence to
    /// read and nothing to settle.
    /// </summary>
    [Fact]
    public async Task ARestartRightAfterAClose_LeavesTheFleetRunning()
    {
        var (first, fleetId) = await StartedLocalFleetAsync(Mate);
        await SeeAsync(first.Services, fleetId, Owner, DateTimeOffset.UtcNow);
        await SeeAsync(first.Services, fleetId, Mate, DateTimeOffset.UtcNow);
        first.KeepDataOnDispose = true;
        var instanceName = first.InstanceName;
        first.Dispose();

        using var reopened = TestClientInstance.Create(instanceName: instanceName);
        var stopped = await reopened.Services.GetRequiredService<LocalFleetAutoStopService>()
            .ReconcileAsync(DateTimeOffset.UtcNow, Ct);

        Assert.Empty(stopped);
        Assert.Equal(FleetActivation.Active, await ActivationOf(reopened.Services, fleetId));
    }

    /// <summary>One pilot still there and the fleet keeps running, exactly as on the server.</summary>
    [Fact]
    public async Task OnePilotStillHeardFrom_KeepsTheLocalFleetRunning()
    {
        using var instance = (await StartedLocalFleetAsync(Mate)).Instance;
        var fleetId = (await instance.Services.GetRequiredService<IFleetRepository>().ListByCreatorAsync(Owner, Ct))
            .Single(fleet => fleet.IsClientOnly).Id;
        await SeeAsync(instance.Services, fleetId, Owner, DateTimeOffset.UtcNow);
        await SeeAsync(instance.Services, fleetId, Mate,
            DateTimeOffset.UtcNow - FleetMemberPresence.SilentAfter - TimeSpan.FromMinutes(5));

        var stopped = await instance.Services.GetRequiredService<LocalFleetAutoStopService>()
            .ReconcileAsync(DateTimeOffset.UtcNow, Ct);

        Assert.Empty(stopped);
        Assert.Equal(FleetActivation.Active, await ActivationOf(instance.Services, fleetId));
    }

    /// <summary>
    /// A fleet nobody has ever published into is not an emptied fleet — it is one whose pilots have not arrived.
    /// A local fleet is created with its owner already on the roster, so without this rule a fleet started and left
    /// standing for an evening would stand itself down on the next launch for the wrong reason.
    /// </summary>
    [Fact]
    public async Task AFleetNobodyHasPublishedIntoYet_IsLeftAlone()
    {
        using var instance = (await StartedLocalFleetAsync(Mate)).Instance;
        var fleetId = (await instance.Services.GetRequiredService<IFleetRepository>().ListByCreatorAsync(Owner, Ct))
            .Single(fleet => fleet.IsClientOnly).Id;

        var stopped = await instance.Services.GetRequiredService<LocalFleetAutoStopService>()
            .ReconcileAsync(DateTimeOffset.UtcNow, Ct);

        Assert.Empty(stopped);
        Assert.Equal(FleetActivation.Active, await ActivationOf(instance.Services, fleetId));
    }

    /// <summary>
    /// The brake is the same one the server runs, so a launch during Tranquility's daily window settles nothing —
    /// even though the timestamps look exactly like the case that does stand a fleet down.
    /// </summary>
    [Fact]
    public async Task LaunchingDuringTheDailyDowntimeWindow_SettlesNothing()
    {
        using var instance = (await StartedLocalFleetAsync(Mate)).Instance;
        var repository = instance.Services.GetRequiredService<IFleetRepository>();
        var fleetId = (await repository.ListByCreatorAsync(Owner, Ct)).Single(fleet => fleet.IsClientOnly).Id;

        var downtime = new DateTimeOffset(2026, 9, 4, 11, 1, 0, TimeSpan.Zero);
        await SeeAsync(instance.Services, fleetId, Owner, downtime - TimeSpan.FromHours(1));
        await SeeAsync(instance.Services, fleetId, Mate, downtime - TimeSpan.FromHours(1));

        Assert.Empty(await instance.Services.GetRequiredService<LocalFleetAutoStopService>().ReconcileAsync(downtime, Ct));
        Assert.Equal(FleetActivation.Active, await ActivationOf(instance.Services, fleetId));

        // Past the window and its reconnect grace the very same data does stand it down — so the test above proved
        // the brake rather than some other reason for nothing happening.
        Assert.Single(await instance.Services.GetRequiredService<LocalFleetAutoStopService>()
            .ReconcileAsync(downtime + TimeSpan.FromMinutes(10), Ct));
    }

    /// <summary>
    /// Only this machine's own fleets. A fleet on a server is the server's to stand down — that is what makes the
    /// rule work when the FC has closed their client altogether — and a client that reached past that would be the
    /// "any member who happened to be last online" the owner ruled out.
    /// </summary>
    [Fact]
    public async Task AServerBackedFleetIsNeverTouchedFromHere()
    {
        using var instance = (await StartedLocalFleetAsync()).Instance;
        var repository = instance.Services.GetRequiredService<IFleetRepository>();
        var local = (await repository.ListByCreatorAsync(Owner, Ct)).Single(fleet => fleet.IsClientOnly);

        // The same fleet in every respect except where it lives.
        local.IsClientOnly = false;
        await repository.UpdateAsync(local, Ct);
        await SeeAsync(instance.Services, local.Id, Owner, DateTimeOffset.UtcNow - TimeSpan.FromDays(2));

        Assert.Empty(await instance.Services.GetRequiredService<LocalFleetAutoStopService>()
            .ReconcileAsync(DateTimeOffset.UtcNow, Ct));
        Assert.Equal(FleetActivation.Active, await ActivationOf(instance.Services, local.Id));
    }

    /// <summary>
    /// The plumbing everything above stands on. A server fleet's presence is stamped by the server off the arriving
    /// stream; a client-only fleet's samples never leave this machine, so without this nobody would ever stamp it and
    /// the roster would hold no record of presence at all — leaving the next start with nothing to measure.
    /// </summary>
    [Fact]
    public async Task AClientOnlyTick_StampsItsMembersLocally()
    {
        var (instance, fleetId) = await StartedLocalFleetAsync();
        using var _ = instance;
        var repository = instance.Services.GetRequiredService<IFleetRepository>();
        Assert.All(await repository.ListMembersAsync(fleetId, Ct), member => Assert.Null(member.LastSeenAt));

        var participation = new FleetParticipation();
        participation.Set([new FleetParticipant(Owner, fleetId, ClientOnly: true)]);
        await new FleetMetricPublisher(
                participation,
                [],
                instance.Services.GetRequiredService<IEventBus>(),
                instance.Services.GetRequiredService<IMetricShareSettings>(),
                instance.Services.GetRequiredService<FleetMemberActivityTracker>())
            .PublishTickAsync(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Ct);

        var stamped = Assert.Single(await repository.ListMembersAsync(fleetId, Ct), m => m.CharacterId == Owner);
        Assert.NotNull(stamped.LastSeenAt);
        Assert.True(DateTimeOffset.UtcNow - stamped.LastSeenAt!.Value < FleetMemberPresence.SilentAfter);
    }

    /// <summary>
    /// And only a client-only one. A server fleet's <c>LastSeenAt</c> belongs to the server that can see every
    /// member's traffic; a client stamping its own row in a local copy would be one machine's opinion of a fleet-wide
    /// question, and would say a pilot was present on a fleet it cannot see the rest of.
    /// </summary>
    [Fact]
    public async Task AServerFleetTick_StampsNothingLocally()
    {
        var (instance, fleetId) = await StartedLocalFleetAsync();
        using var _ = instance;
        var repository = instance.Services.GetRequiredService<IFleetRepository>();

        var participation = new FleetParticipation();
        participation.Set([new FleetParticipant(Owner, fleetId, ClientOnly: false)]);
        await new FleetMetricPublisher(
                participation,
                [],
                instance.Services.GetRequiredService<IEventBus>(),
                instance.Services.GetRequiredService<IMetricShareSettings>(),
                instance.Services.GetRequiredService<FleetMemberActivityTracker>())
            .PublishTickAsync(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Ct);

        Assert.All(await repository.ListMembersAsync(fleetId, Ct), member => Assert.Null(member.LastSeenAt));
    }

}
