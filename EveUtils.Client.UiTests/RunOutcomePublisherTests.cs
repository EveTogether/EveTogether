using Avalonia.Headless.XUnit;
using EveUtils.Client.Messaging;
using EveUtils.Client.Notifications;
using EveUtils.Client.Runs;
using EveUtils.Client.Transport;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.RunOutcomes.Dtos;
using EveUtils.Shared.Modules.RunOutcomes.Enums;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Earnings;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Events;
using EveUtils.Shared.Modules.Runs.Queries;
using EveUtils.Shared.Transport;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-515: a saved run's outcome goes to a server the pilot opted in to, without a press, and only then. The server is
/// a recording double and the connection state is driven by hand, so every branch on the way to the wire is visible.
/// </summary>
public sealed class RunOutcomePublisherTests
{
    private const long Pilot = 90000001;
    private const string Server = "https://krah.invalid";
    private const string OtherServer = "https://alliance.invalid";
    private const int SiteTypeId = 2311;
    private const int Tritanium = 34;
    private static readonly DateTime StartedAtUtc = DateTime.UtcNow.Date.AddDays(-10);
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Opt-in: nothing is shared with a server the pilot has not chosen for, however many runs are saved, and
    /// the one question is asked once. Counter-proof: skip the gate and the first save shares.</summary>
    [AvaloniaFact]
    public async Task Save_WithoutAnAnswer_SharesNothing_AndAsksOnce()
    {
        using Fixture fixture = await Fixture.CreateAsync();

        await fixture.SaveRunAsync(StartedAtUtc);
        await fixture.SaveRunAsync(StartedAtUtc.AddHours(1));

        Assert.Empty(fixture.Outcomes.Shares);
        var offer = Assert.Single(fixture.Toasts.ActionToasts);
        Assert.Equal(["SHARE", "NOT NOW", "NEVER"], offer.Actions.Select(action => action.Label));
        Assert.Contains("Help", offer.Title);
        Assert.DoesNotContain("Angel Sanctum", offer.Title + offer.Message);
    }

    /// <summary>Opted in: SAVE shares exactly one outcome, keyed by the run, from the pilot's own session.
    /// Counter-proof: without the publisher's subscription nothing is sent.</summary>
    [AvaloniaFact]
    public async Task Save_AfterOptIn_SharesExactlyOneOutcome()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        await fixture.OptInAsync();

        Guid runId = await fixture.SaveRunAsync(StartedAtUtc);

        Share share = Assert.Single(fixture.Outcomes.Shares);
        Assert.Equal((Server, Pilot, runId.ToString()), (share.Server, share.CharacterId, share.ActivityRef));
        Assert.NotNull(share.Outcome);
        Assert.Equal(DateOnly.FromDateTime(StartedAtUtc.AddMinutes(15)), share.Outcome.CompletedOn);
        Assert.Equal(OutcomeShareState.Shared, Assert.Single(await fixture.RecordsAsync()).State);
    }

    /// <summary>What leaves the machine carries no location, name, fit or time of day: the serialised request holds the
    /// listed fields and none of the values a run is tied to a pilot or a place by. Counter-proof: a character id put
    /// into the key's name, or a system id anywhere in the request, fails it.</summary>
    [AvaloniaFact]
    public async Task SharedRecord_CarriesNoSystemSignatureFitNameOrTimeOfDay()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        await fixture.OptInAsync();
        await fixture.SaveRunAsync(StartedAtUtc);

        Share share = Assert.Single(fixture.Outcomes.Shares);
        string json = JsonFormatter.Default.Format(ServerRunOutcomeClient.ToRequest(share.ActivityRef, share.Revision, share.Outcome));

        string[] fields = [.. JsonDocument.Parse(json).RootElement.EnumerateObject().Select(property => property.Name)];
        Assert.Equal(["activityRef", "revision", "key", "completedOn", "durationSeconds", "fixedIsk", "fleetSize", "lootBasis", "loot"],
            fields);
        foreach (string forbidden in new[] { "30000142", Pilot.ToString(), "Jithran", "Angel Sanctum", "system", "signature", "fit" })
            Assert.DoesNotContain(forbidden, json, StringComparison.OrdinalIgnoreCase);
        Assert.Matches(@"""completedOn"": ""\d{4}-\d{2}-\d{2}""", json);
    }

    /// <summary>A correction is an update, not a second row: the same activity goes again under a higher revision. A
    /// signal that changed nothing sends nothing. Counter-proof: send on every signal and there are three shares.</summary>
    [AvaloniaFact]
    public async Task Correction_SharesAgainUnderAHigherRevision_AndAnUnchangedSignalSharesNothing()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        await fixture.OptInAsync();
        Guid runId = await fixture.SaveRunAsync(StartedAtUtc);

        await fixture.Send(new SetRunLootManualCommand(runId, StartedAtUtc.AddMinutes(20),
            [new RunLootEntryInput { ItemTypeId = Tritanium, Name = "Tritanium", Quantity = 30, LootKind = LootKind.Gained }]));
        await fixture.Publisher.WhenIdleAsync();
        await fixture.Bus.PublishAsync(new RunsChangedEvent(runId), EventTarget.Local, Token);
        await fixture.Publisher.WhenIdleAsync();

        Assert.Equal(2, fixture.Outcomes.Shares.Count);
        Assert.All(fixture.Outcomes.Shares, share => Assert.Equal(runId.ToString(), share.ActivityRef));
        Assert.True(fixture.Outcomes.Shares[1].Revision > fixture.Outcomes.Shares[0].Revision);
        Assert.True(fixture.Outcomes.Shares[1].Outcome?.LootLines.Single().Quantity > 10);
        Assert.Single(await fixture.RecordsAsync());
    }

    /// <summary>Delete after sharing sends a tombstone above the shared revision and forgets the record; a run that was
    /// never shared sends nothing. Counter-proof: ignore the delete and the server keeps an outcome of a run that is gone.</summary>
    [AvaloniaFact]
    public async Task Delete_AfterSharing_SendsATombstone()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        await fixture.OptInAsync();
        Guid runId = await fixture.SaveRunAsync(StartedAtUtc);
        Guid neverShared = await fixture.StartRunAsync(StartedAtUtc.AddHours(2));

        await fixture.Send(new DeleteRunCommand(runId, DateTime.UtcNow));
        await fixture.Send(new DiscardRunCommand(neverShared, StartedAtUtc.AddHours(3)));
        await fixture.Publisher.WhenIdleAsync();

        Assert.Equal(2, fixture.Outcomes.Shares.Count);
        Share tombstone = fixture.Outcomes.Shares[1];
        Assert.Equal(runId.ToString(), tombstone.ActivityRef);
        Assert.Null(tombstone.Outcome);
        Assert.True(tombstone.Revision >= fixture.Outcomes.Shares[0].Revision);
        Assert.Empty(await fixture.RecordsAsync());
    }

    /// <summary>Opting out withdraws what the pilot's characters shared and nothing more is pushed afterwards.
    /// Counter-proof: switch the choice without withdrawing and the server keeps both outcomes.</summary>
    [AvaloniaFact]
    public async Task OptOut_WithdrawsEverythingShared_AndPushesNothingAfterwards()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        await fixture.OptInAsync();
        await fixture.SaveRunAsync(StartedAtUtc);
        await fixture.SaveRunAsync(StartedAtUtc.AddHours(1));
        fixture.Outcomes.WithdrawCount = 2;

        await fixture.Publisher.SetChoiceAsync(Server, OutcomeSharingChoice.NotShared, backfill: false);
        await fixture.Publisher.WhenIdleAsync();
        await fixture.SaveRunAsync(StartedAtUtc.AddHours(2));

        Assert.Equal([(Server, Pilot)], fixture.Outcomes.Withdraws);
        Assert.Equal(2, fixture.Outcomes.Shares.Count);
        Assert.Empty(await fixture.RecordsAsync());
        Assert.Contains(fixture.Toasts.Toasts, toast => toast.Title == "Your 2 shared outcomes were removed from " + Server + ".");
        Assert.Empty(fixture.Toasts.ActionToasts);
    }

    /// <summary>The opt-in offers the earlier runs of the last 90 days and shares those, not the older ones and not the
    /// ones another server already holds. Without the choice only new runs go. Counter-proof: widen the window to 120
    /// days and the old run goes too.</summary>
    [AvaloniaTheory]
    [InlineData(true, 2)]
    [InlineData(false, 0)]
    public async Task OptIn_ShareEarlierRuns_SharesThoseOfTheLastNinetyDays(bool backfill, int expectedShares)
    {
        using Fixture fixture = await Fixture.CreateAsync();
        DateTime now = DateTime.UtcNow;
        await fixture.SaveRunAsync(now.AddDays(-100));
        await fixture.SaveRunAsync(now.AddDays(-60));
        await fixture.SaveRunAsync(now.AddDays(-5));
        Assert.Equal(2, await fixture.Publisher.BackfillCountAsync(Server, Token));

        await fixture.Publisher.SetChoiceAsync(Server, OutcomeSharingChoice.Shared, backfill);
        await fixture.Publisher.WhenIdleAsync();

        Assert.Equal(expectedShares, fixture.Outcomes.Shares.Count);
        Assert.All(fixture.Outcomes.Shares, share => Assert.True(share.Outcome?.CompletedOn >= DateOnly.FromDateTime(now.AddDays(-90))));
    }

    /// <summary>An unreachable server leaves the outcome queued and tries again after a wait, without anyone pressing
    /// anything; the record says why in between. Counter-proof: drop the failure and the outcome is never sent.</summary>
    [AvaloniaFact]
    public async Task UnreachableServer_IsRetriedAfterABackoff()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        await fixture.OptInAsync();
        fixture.Outcomes.Results.Enqueue(OutcomeCallResult.Transient("Could not reach the server: down"));
        fixture.Publisher.Backoff = [TimeSpan.Zero];

        await fixture.SaveRunAsync(StartedAtUtc);
        await fixture.WaitUntilAsync(() => fixture.Outcomes.Shares.Count == 2);
        await fixture.Publisher.WhenIdleAsync();

        OutcomeShareRecordDto record = Assert.Single(await fixture.RecordsAsync());
        Assert.Equal(OutcomeShareState.Shared, record.State);
        Assert.Null(record.LastError);
    }

    /// <summary>The failure is said while it lasts: the status carries the waiting count and the reason.</summary>
    [AvaloniaFact]
    public async Task UnreachableServer_IsReportedInTheStatus_WhileItLasts()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        await fixture.OptInAsync();
        fixture.Outcomes.Results.Enqueue(OutcomeCallResult.Transient("Could not reach the server: down"));
        fixture.Publisher.Backoff = [TimeSpan.FromHours(1)];

        await fixture.SaveRunAsync(StartedAtUtc);

        OutcomeSharingStatus status = await fixture.Publisher.StatusAsync(Server, Token);
        Assert.Equal((0, 1, "Could not reach the server: down"), (status.SharedCount, status.PendingCount, status.Problem));
        Assert.Single(fixture.Outcomes.Shares);
    }

    /// <summary>A server that switched sharing off (PERMISSION_DENIED) is not retried, and the status says so.
    /// Counter-proof: treat it like an outage and the share is attempted again at once.</summary>
    [AvaloniaFact]
    public async Task ServerSwitchedOff_IsNotRetried_AndTheStatusSaysSo()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        await fixture.OptInAsync();
        fixture.Outcomes.Results.Enqueue(new OutcomeCallResult(false, MessageCodes.PermissionDenied, "off"));
        fixture.Publisher.Backoff = [TimeSpan.Zero];

        await fixture.SaveRunAsync(StartedAtUtc);

        Assert.Single(fixture.Outcomes.Shares);
        OutcomeSharingStatus status = await fixture.Publisher.StatusAsync(Server, Token);
        Assert.Equal("The server has switched earnings sharing off.", status.Problem);
        Assert.Equal(1, status.PendingCount);
    }

    /// <summary>Saved while offline: it waits for the pilot's connection and goes the moment it is back.</summary>
    [AvaloniaFact]
    public async Task SavedWhileOffline_IsSharedWhenTheConnectionIsBack()
    {
        using Fixture fixture = await Fixture.CreateAsync(connected: false);
        await fixture.OptInAsync();

        await fixture.SaveRunAsync(StartedAtUtc);
        Assert.Empty(fixture.Outcomes.Shares);

        fixture.Connector.RaiseCharacterStateChanged(Server, (int)Pilot, ServerConnectionState.Connected);
        await fixture.Publisher.WhenIdleAsync();

        Assert.Single(fixture.Outcomes.Shares);
    }

    /// <summary>A fleet mate shared the activity first: nothing of this pilot's is on the server, so a later delete owes
    /// it no tombstone.</summary>
    [AvaloniaFact]
    public async Task ActivitySharedByAFleetMateFirst_IsCovered_AndDeletedWithoutATombstone()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        await fixture.OptInAsync();
        fixture.Outcomes.Results.Enqueue(new OutcomeCallResult(true, "", "SharedByAnother", RunOutcomeShareStatus.SharedByAnother));

        Guid runId = await fixture.SaveRunAsync(StartedAtUtc);
        Assert.Equal(OutcomeShareState.Covered, Assert.Single(await fixture.RecordsAsync()).State);
        await fixture.Send(new DeleteRunCommand(runId, DateTime.UtcNow));
        await fixture.Publisher.WhenIdleAsync();

        Assert.Single(fixture.Outcomes.Shares);
        Assert.Empty(await fixture.RecordsAsync());
    }

    /// <summary>Opted in to two servers, an activity still goes to exactly one: the one holding the most of this
    /// pilot's outcomes, else the first by address. Counter-proof: send to every opted-in server and there are two.</summary>
    [AvaloniaFact]
    public async Task OptedInToTwoServers_AnActivityGoesToExactlyOne()
    {
        using Fixture fixture = await Fixture.CreateAsync(coupleOtherServer: true);
        await fixture.OptInAsync();
        await fixture.Publisher.SetChoiceAsync(OtherServer, OutcomeSharingChoice.Shared, backfill: false);

        await fixture.SaveRunAsync(StartedAtUtc);

        Assert.Equal(OtherServer, Assert.Single(fixture.Outcomes.Shares).Server);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TestClientInstance _instance;

        private Fixture(TestClientInstance instance, RecordingOutcomeServer outcomes, FakeRemoteBusConnector connector,
            RecordingToastService toasts)
        {
            _instance = instance;
            Outcomes = outcomes;
            Connector = connector;
            Toasts = toasts;
        }

        public RecordingOutcomeServer Outcomes { get; }
        public FakeRemoteBusConnector Connector { get; }
        public RecordingToastService Toasts { get; }
        public IDispatcher Dispatcher => _instance.Services.GetRequiredService<IDispatcher>();
        public IEventBus Bus => _instance.Services.GetRequiredService<IEventBus>();
        public RunOutcomePublisher Publisher => _instance.Services.GetRequiredService<RunOutcomePublisher>();

        public static async Task<Fixture> CreateAsync(bool connected = true, bool coupleOtherServer = false)
        {
            var outcomes = new RecordingOutcomeServer();
            var connector = new FakeRemoteBusConnector();
            var toasts = new RecordingToastService();
            var instance = TestClientInstance.Create(services =>
            {
                services.AddSingleton<IRemoteBusConnector>(connector);
                services.AddSingleton<IServerRunOutcomeClient>(outcomes);
                services.AddSingleton<IToastService>(toasts);
            });
            IClientSessionStore sessions = instance.Services.GetRequiredService<IClientSessionStore>();
            await sessions.SaveAsync(Server, new ClientSessionTokens("access", "refresh", "Jithran", (int)Pilot), Token);
            if (coupleOtherServer)
                await sessions.SaveAsync(OtherServer, new ClientSessionTokens("access", "refresh", "Jithran", (int)Pilot), Token);
            if (connected)
            {
                connector.RaiseCharacterStateChanged(Server, (int)Pilot, ServerConnectionState.Connected);
                connector.RaiseCharacterStateChanged(OtherServer, (int)Pilot, ServerConnectionState.Connected);
            }

            var fixture = new Fixture(instance, outcomes, connector, toasts);
            await fixture.Publisher.WhenIdleAsync();
            return fixture;
        }

        public async Task OptInAsync()
        {
            await Publisher.SetChoiceAsync(Server, OutcomeSharingChoice.Shared, backfill: false);
            await Publisher.WhenIdleAsync();
        }

        public async Task<Guid> StartRunAsync(DateTime startedAtUtc)
        {
            Result<Guid> started = await Dispatcher.Send(new StartRunCommand(Pilot, ActivityKind.Site, startedAtUtc, SiteTypeId,
                "Angel Sanctum", 30000142), Token);
            Assert.True(started.IsSuccess);
            return started.Value;
        }

        public async Task<Guid> SaveRunAsync(DateTime startedAtUtc)
        {
            Guid runId = await StartRunAsync(startedAtUtc);
            await Send(new SaveRunCommand(runId, startedAtUtc.AddMinutes(15), startedAtUtc.AddMinutes(16),
            [
                new RunLootCaptureInput
                {
                    CapturedAtUtc = startedAtUtc.AddMinutes(10), Source = LootCaptureSource.Clipboard, ContentHash = runId.ToString(),
                    Entries = [new RunLootEntryInput { ItemTypeId = Tritanium, Name = "Tritanium", Quantity = 10, LootKind = LootKind.Gained }]
                }
            ], [], [], []));
            await Publisher.WhenIdleAsync();
            return runId;
        }

        /// <summary>A retry waits out its backoff on its own timer, which <see cref="RunOutcomePublisher.WhenIdleAsync"/>
        /// does not cover; a short poll with a ceiling stands in for it.</summary>
        public async Task WaitUntilAsync(Func<bool> condition)
        {
            for (int attempt = 0; attempt < 200 && !condition(); attempt++)
                await Task.Delay(25, Token);
        }

        public async Task Send(ICommand<Result> command) => Assert.True((await Dispatcher.Send(command, Token)).IsSuccess);

        public async Task Send<T>(ICommand<Result<T>> command) => Assert.True((await Dispatcher.Send(command, Token)).IsSuccess);

        public async Task<IReadOnlyList<OutcomeShareRecordDto>> RecordsAsync() =>
            (await Dispatcher.Query(new GetOutcomeShareRecordsQuery(), Token)).Value ?? [];

        public void Dispose() => _instance.Dispose();
    }

    private sealed record Share(string Server, long CharacterId, string ActivityRef, long Revision, RunOutcomeShareDto? Outcome);

    /// <summary>The server as far as the client can tell: it takes or refuses a share, and counts a withdraw. A queued
    /// result answers the next call; with none queued it stores.</summary>
    private sealed class RecordingOutcomeServer : IServerRunOutcomeClient
    {
        private readonly Lock _gate = new();
        private readonly List<Share> _shares = [];
        private readonly List<(string, long)> _withdraws = [];

        public Queue<OutcomeCallResult> Results { get; } = new();
        public int WithdrawCount { get; set; }

        public IReadOnlyList<Share> Shares
        {
            get { lock (_gate) return [.. _shares]; }
        }

        public IReadOnlyList<(string Server, long CharacterId)> Withdraws
        {
            get { lock (_gate) return [.. _withdraws]; }
        }

        public Task<OutcomeCallResult> ShareAsync(string serverAddress, long characterId, string activityRef, long revision,
            RunOutcomeShareDto? outcome, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                _shares.Add(new Share(serverAddress, characterId, activityRef, revision, outcome));
                return Task.FromResult(Results.TryDequeue(out OutcomeCallResult? next)
                    ? next
                    : new OutcomeCallResult(true, "", "Stored", RunOutcomeShareStatus.Stored));
            }
        }

        public Task<OutcomeCallResult> WithdrawAsync(string serverAddress, long characterId, CancellationToken cancellationToken = default)
        {
            lock (_gate)
                _withdraws.Add((serverAddress, characterId));
            return Task.FromResult(new OutcomeCallResult(true, "", "Outcomes withdrawn.", Count: WithdrawCount));
        }

        public Task<OutcomeReadResult<ActivityOutcomesDto>> GetOutcomesAsync(string serverAddress, long characterId,
            ActivityStatsKey key, DateOnly since, CancellationToken cancellationToken = default) =>
            Task.FromResult(OutcomeReadResult<ActivityOutcomesDto>.Transient("not used"));

        public Task<OutcomeReadResult<ActivityOutcomeLootDto>> GetOutcomeLootAsync(string serverAddress, long characterId,
            ActivityStatsKey key, DateOnly since, CancellationToken cancellationToken = default) =>
            Task.FromResult(OutcomeReadResult<ActivityOutcomeLootDto>.Transient("not used"));
    }
}
