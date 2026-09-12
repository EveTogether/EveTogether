using Avalonia.Headless.XUnit;
using EveUtils.Client.Fleet;
using EveUtils.Client.Messaging;
using EveUtils.Client.Runs;
using EveUtils.Client.Transport;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Events;
using EveUtils.Shared.Modules.Settings.Commands;
using EveUtils.Shared.Transport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-245: a run flown in a fleet on a coupled server reaches that fleet's server without anyone pressing PUBLISH, and
/// a group mate's run reaches this client the moment the server says it arrived. Measured by Jithran and Raymond on
/// 2026-09-11: whoever published first never saw the other's run until pressing PUBLISH a second time, because the
/// button was the only thing that ever pushed or pulled.
///
/// The server is a recording double throughout — nothing leaves the machine — and the connection state is driven by
/// hand, so every branch the publisher takes on the way to the wire is visible.
/// </summary>
public sealed class FleetRunAutoPublishTests
{
    private const long Pilot = 90000001;
    private const long Mate = 90000002;
    private const long FleetId = 7;
    private const string FleetServer = "https://fleet.invalid";
    private const string OtherServer = "https://other.invalid";
    private static readonly DateTime StartedAtUtc = new(2026, 9, 11, 18, 49, 0, DateTimeKind.Utc);
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>AC-1, both saves: the pilot's own SAVE and the app's own save of a run left a day (ET-179) publish a
    /// fleet run to its fleet's server by themselves. Counter-proof: without the publisher nothing is pushed and the
    /// run stays Local.</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Save_FleetRunOfAFleetOnACoupledServer_IsPublishedToThatServerWithoutAPress(bool savedByTheApp)
    {
        using Fixture fixture = await Fixture.CreateAsync();

        Guid runId = await fixture.StartFleetRunAsync();
        if (savedByTheApp)
        {
            await fixture.Send(new SetRunStoppedCommand(runId, StartedAtUtc.AddMinutes(15)));
            await fixture.Send(new SaveRunsLeftUnfinishedCommand(StartedAtUtc.AddDays(2)));
        }
        else
            await fixture.SaveAsync(runId);
        await fixture.Publisher.WhenIdleAsync();

        Assert.Equal([(FleetServer, runId, Pilot)], fixture.Server.Pushes);
        Run run = await fixture.RunAsync(runId);
        Assert.Equal(RunSyncState.Synced, run.SyncState);
        Assert.Equal(FleetServer, run.SyncServerAddress);
    }

    /// <summary>AC-3: a solo run keeps waiting for PUBLISH. Counter-proof: publish every saved run of a coupled pilot
    /// and this one is pushed.</summary>
    [AvaloniaFact]
    public async Task Save_SoloRun_IsNotPublished()
    {
        using Fixture fixture = await Fixture.CreateAsync();

        Result<Guid> started = await fixture.Dispatcher.Send(new StartRunCommand(Pilot, ActivityKind.Site, StartedAtUtc,
            1234, "Blood Refuge", 30000142), Token);
        await fixture.SaveAsync(started.Value);
        await fixture.Publisher.WhenIdleAsync();

        Assert.Empty(fixture.Server.Pushes);
        Assert.Equal(RunSyncState.Local, (await fixture.RunAsync(started.Value)).SyncState);
    }

    /// <summary>AC-5: switched off in the settings, nothing publishes by itself. Counter-proof: ignore the setting and
    /// the run is pushed.</summary>
    [AvaloniaFact]
    public async Task Save_WithAutoPublishSwitchedOff_PublishesNothing()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        await fixture.Send(new SetSettingCommand(FleetRunAutoPublisher.EnabledSettingKey, "false"));

        Guid runId = await fixture.StartFleetRunAsync();
        await fixture.SaveAsync(runId);
        await fixture.Publisher.WhenIdleAsync();

        Assert.Empty(fixture.Server.Pushes);
        Assert.Equal(RunSyncState.Local, (await fixture.RunAsync(runId)).SyncState);
    }

    /// <summary>The server is the fleet's own, never a guess. A fleet id is only unique per server, so the same id on a
    /// second server — or on a fleet that lives only on this client — leaves the fleet's server unknown, and the run
    /// waits for PUBLISH rather than going to whichever server answered first. Counter-proof: take the first
    /// participant's server and the run goes to it.</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Save_FleetIdThatCannotBeToldApart_IsPublishedNowhere(bool alsoClientOnly)
    {
        using Fixture fixture = await Fixture.CreateAsync(
        [
            new FleetParticipant((int)Pilot, FleetId, ClientOnly: false, ServerAddress: FleetServer),
            alsoClientOnly
                ? new FleetParticipant((int)Mate, FleetId, ClientOnly: true)
                : new FleetParticipant((int)Mate, FleetId, ClientOnly: false, ServerAddress: OtherServer)
        ]);

        Guid runId = await fixture.StartFleetRunAsync();
        await fixture.SaveAsync(runId);
        await fixture.Publisher.WhenIdleAsync();

        Assert.Empty(fixture.Server.Pushes);
        Run run = await fixture.RunAsync(runId);
        Assert.Equal(RunSyncState.Local, run.SyncState);
        Assert.Null(await fixture.OriginServerAsync(run.GroupCode));
    }

    /// <summary>AC-2 (ET-215): a correction to a published fleet run goes again by itself, rather than waiting as
    /// "changed since published" for a press. Counter-proof: leave an Outdated run alone and there is one push.</summary>
    [AvaloniaFact]
    public async Task CorrectionAfterPublishing_GoesToTheServerAgain()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        Guid runId = await fixture.StartFleetRunAsync();
        await fixture.SaveAsync(runId);
        await fixture.Publisher.WhenIdleAsync();

        await fixture.Send(new SetRunLootManualCommand(runId, StartedAtUtc.AddMinutes(20),
            [new RunLootEntryInput { ItemTypeId = 34, Name = "Tritanium", Quantity = 3, LootKind = LootKind.Gained }]));
        await fixture.Publisher.WhenIdleAsync();

        Assert.Equal([(FleetServer, runId, Pilot), (FleetServer, runId, Pilot)], fixture.Server.Pushes);
        Assert.Equal(RunSyncState.Synced, (await fixture.RunAsync(runId)).SyncState);
    }

    /// <summary>AC-4: a refused publish is said on the row, with RETRY, and the run stays queued for the next try;
    /// RETRY sends it without asking again. Counter-proof: drop the failure on the floor and the row reads "queued" with
    /// nothing to press — the silent hang the ticket rules out.</summary>
    [AvaloniaFact]
    public async Task RefusedPublish_SaysSoOnTheRow_AndRetryPublishesIt()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        fixture.Server.Accepts = false;
        Guid runId = await fixture.StartFleetRunAsync();
        await fixture.SaveAsync(runId);
        await fixture.Publisher.WhenIdleAsync();

        RunsOverviewViewModel overview = await fixture.OverviewAsync();
        string serverName = overview.Tabs.Single(tab => tab.ServerAddress == FleetServer).Header;
        ActivityOverviewRowViewModel failed = _Row(overview);
        Assert.True(failed.HasPublishFailure);
        Assert.Equal($"publishing to {serverName} failed", failed.SyncText);
        Assert.Equal("The server said no.", failed.PublishFailureText);
        Assert.Equal(RunSyncState.Pending, (await fixture.RunAsync(runId)).SyncState);

        fixture.Server.Accepts = true;
        await failed.RetryPublishCommand.ExecuteAsync(null);
        await fixture.Publisher.WhenIdleAsync();
        await overview.LoadAsync(Token);

        ActivityOverviewRowViewModel retried = _Row(overview);
        Assert.False(retried.HasPublishFailure);
        Assert.Equal($"published to {serverName}", retried.SyncText);
        Assert.Equal(RunSyncState.Synced, (await fixture.RunAsync(runId)).SyncState);
    }

    /// <summary>Offline at SAVE: the run is queued for its fleet's server and goes the moment this pilot's connection is
    /// back, without a second publish. Counter-proof: skip the reconnect catch-up and the run stays queued.</summary>
    [AvaloniaFact]
    public async Task SavedWhileOffline_GoesWhenTheConnectionIsBack()
    {
        using Fixture fixture = await Fixture.CreateAsync(connected: false);
        Guid runId = await fixture.StartFleetRunAsync();
        await fixture.SaveAsync(runId);
        await fixture.Publisher.WhenIdleAsync();
        Assert.Empty(fixture.Server.Pushes);
        Assert.Equal(RunSyncState.Pending, (await fixture.RunAsync(runId)).SyncState);

        fixture.Connector.RaiseCharacterStateChanged(FleetServer, (int)Pilot, ServerConnectionState.Connected);
        await fixture.Publisher.WhenIdleAsync();

        Assert.Equal([(FleetServer, runId, Pilot)], fixture.Server.Pushes);
        Assert.Equal(RunSyncState.Synced, (await fixture.RunAsync(runId)).SyncState);
    }

    /// <summary>The comment of 2026-09-11, end to end on Jithran's side: he publishes first, Raymond after. The server's
    /// notice that Raymond's run arrived is enough for Jithran's client to pull it — from the server that sent the
    /// notice — and show the group with both pilots, without either pressing anything. Counter-proof: without the
    /// notice handler nothing is pulled and the group holds Jithran's run alone.</summary>
    [AvaloniaFact]
    public async Task GroupMatesRunArriving_IsPulledFromTheServerThatSaidSo()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        Guid runId = await fixture.StartFleetRunAsync();
        await fixture.SaveAsync(runId);
        await fixture.Publisher.WhenIdleAsync();
        string groupCode = (await fixture.RunAsync(runId)).GroupCode ?? throw new InvalidOperationException("No group.");
        Guid mateRunId = fixture.Server.Hold(Mate, groupCode);
        int pullsBefore = fixture.Server.Pulls.Count;

        await fixture.Bus.PublishAsync(new RunGroupUpdatedEvent(new RunGroupUpdate(groupCode))
            { SourceServerAddress = FleetServer }, EventTarget.Local, Token);
        await fixture.Publisher.WhenIdleAsync();

        Assert.Equal([(FleetServer, groupCode, Pilot)], fixture.Server.Pulls.Skip(pullsBefore));
        Run mateRun = await fixture.RunAsync(mateRunId);
        Assert.Equal(Mate, mateRun.CharacterId);
        Assert.Equal(groupCode, mateRun.GroupCode);
        Assert.Single(fixture.Server.Pushes);
    }

    private static ActivityOverviewRowViewModel _Row(RunsOverviewViewModel overview) =>
        Assert.Single(Assert.Single(overview.Tabs[0].Days).Rows);

    private sealed class Fixture : IDisposable
    {
        private readonly TestClientInstance _instance;

        private Fixture(TestClientInstance instance, RecordingRunServer server, FakeRemoteBusConnector connector)
        {
            _instance = instance;
            Server = server;
            Connector = connector;
        }

        public RecordingRunServer Server { get; }
        public FakeRemoteBusConnector Connector { get; }
        public IDispatcher Dispatcher => _instance.Services.GetRequiredService<IDispatcher>();
        public IEventBus Bus => _instance.Services.GetRequiredService<IEventBus>();
        public FleetRunAutoPublisher Publisher => _instance.Services.GetRequiredService<FleetRunAutoPublisher>();

        /// <summary>The pilot coupled to the fleet's server and in fleet 7 there; <paramref name="participants"/>
        /// replaces that membership when a test needs another.</summary>
        public static async Task<Fixture> CreateAsync(IReadOnlyList<FleetParticipant>? participants = null,
            bool connected = true)
        {
            var server = new RecordingRunServer();
            var connector = new FakeRemoteBusConnector();
            var instance = TestClientInstance.Create(services =>
            {
                services.AddSingleton<IRemoteBusConnector>(connector);
                services.AddSingleton<IServerRunSyncClient>(server);
            });
            await instance.Services.GetRequiredService<IClientSessionStore>()
                .SaveAsync(FleetServer, new ClientSessionTokens("access", "refresh", "Jithran", (int)Pilot), Token);
            instance.Services.GetRequiredService<IFleetParticipation>().Set(participants
                ?? [new FleetParticipant((int)Pilot, FleetId, ClientOnly: false, ServerAddress: FleetServer)]);
            if (connected)
                connector.RaiseCharacterStateChanged(FleetServer, (int)Pilot, ServerConnectionState.Connected);
            var fixture = new Fixture(instance, server, connector);
            await fixture.Publisher.WhenIdleAsync();
            return fixture;
        }

        /// <summary>The commander's own start: the code is minted here and its fleet recorded with it (ET-182).</summary>
        public async Task<Guid> StartFleetRunAsync()
        {
            Result<Guid> started = await Dispatcher.Send(new StartRunCommand(Pilot, ActivityKind.Abyssal, StartedAtUtc,
                1234, null, 30000142, FleetId: FleetId, IsFleetCommander: true), Token);
            Assert.True(started.IsSuccess);
            return started.Value;
        }

        public async Task SaveAsync(Guid runId) =>
            await Send(new SaveRunCommand(runId, StartedAtUtc.AddMinutes(15), StartedAtUtc.AddMinutes(16), [], [], [], []));

        public async Task Send(ICommand<Result> command) => Assert.True((await Dispatcher.Send(command, Token)).IsSuccess);

        public async Task Send<T>(ICommand<Result<T>> command) => Assert.True((await Dispatcher.Send(command, Token)).IsSuccess);

        public async Task<Run> RunAsync(Guid runId)
        {
            await using ClientDbContext db = await _Db();
            return await db.Set<Run>().AsNoTracking().SingleAsync(run => run.Id == runId, Token);
        }

        public async Task<string?> OriginServerAsync(string? groupCode)
        {
            await using ClientDbContext db = await _Db();
            return await db.Set<RunGroupOrigin>().AsNoTracking()
                .Where(origin => origin.GroupCode == groupCode)
                .Select(origin => origin.ServerAddress)
                .SingleAsync(Token);
        }

        public async Task<RunsOverviewViewModel> OverviewAsync()
        {
            await Dispatcher.Send(new RebuildActivitySummariesCommand(), Token);
            // No lane clock: nothing closes this view-model, so a DispatcherTimer would tick on for the rest of the run.
            var overview = new RunsOverviewViewModel(Dispatcher, new RecordingDialogService(), _instance.Services,
                [new Character("Jithran", (int)Pilot)], runClock: false);
            await overview.LoadAsync(Token);
            return overview;
        }

        private Task<ClientDbContext> _Db() => _instance.Services
            .GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(Token);

        public void Dispose() => _instance.Dispose();
    }

    /// <summary>The server as far as the client can tell: it accepts or refuses a push, and answers a pull with the runs
    /// it holds for the asked groups. Records both, from whatever thread the publisher works on.</summary>
    private sealed class RecordingRunServer : IServerRunSyncClient
    {
        private readonly Lock _gate = new();
        private readonly List<(string, Guid, long)> _pushes = [];
        private readonly List<(string, string, long)> _pulls = [];
        private readonly List<RunWirePayload> _held = [];

        public bool Accepts { get; set; } = true;

        public IReadOnlyList<(string Server, Guid RunId, long CharacterId)> Pushes
        {
            get { lock (_gate) return [.. _pushes]; }
        }

        public IReadOnlyList<(string Server, string GroupCode, long CharacterId)> Pulls
        {
            get { lock (_gate) return [.. _pulls]; }
        }

        /// <summary>A group mate's saved run the server already holds.</summary>
        public Guid Hold(long characterId, string groupCode)
        {
            var run = new Run
            {
                Id = Guid.CreateVersion7(), CharacterId = characterId, GroupCode = groupCode, ActivityKind = ActivityKind.Abyssal,
                State = RunState.Saved, StartedAtUtc = StartedAtUtc, StoppedAtUtc = StartedAtUtc.AddMinutes(14),
                SavedAtUtc = StartedAtUtc.AddMinutes(15), SiteTypeId = 1234, CharacterNameSnapshot = "Raymond",
                SyncState = RunSyncState.Synced, LastPushedAtUtc = DateTime.UtcNow, Revision = 2
            };
            lock (_gate)
                _held.Add(new RunWirePayload
                {
                    Run = RunWireData.FromEntity(run), SentAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                });
            return run.Id;
        }

        public Task<(bool Accepted, string Message, DateTime? LastPushedAtUtc)> PushAsync(
            string serverAddress, RunWirePayload payload, long actingCharacterId, CancellationToken cancellationToken = default)
        {
            lock (_gate)
                if (Accepts)
                    _pushes.Add((serverAddress, payload.Run.Id, actingCharacterId));
            return Task.FromResult(Accepts
                ? (true, "Run synced.", (DateTime?)DateTime.UtcNow)
                : (false, "The server said no.", null));
        }

        public Task<(bool Accepted, string Message, IReadOnlyList<RunWirePayload> Runs)> PullAsync(
            string serverAddress, IReadOnlyCollection<string> groupCodes, DateTime sinceUtc, long actingCharacterId,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                foreach (string groupCode in groupCodes)
                    _pulls.Add((serverAddress, groupCode, actingCharacterId));
                IReadOnlyList<RunWirePayload> runs = [.. _held.Where(payload => groupCodes.Contains(payload.Run.GroupCode ?? ""))];
                return Task.FromResult((true, "Runs synchronized.", runs));
            }
        }
    }
}
