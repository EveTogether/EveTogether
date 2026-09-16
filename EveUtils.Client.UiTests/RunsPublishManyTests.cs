using Avalonia.Headless.XUnit;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Messaging;
using EveUtils.Client.Transport;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Data;
using EveUtils.Shared.Transport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using ICqrsDispatcher = EveUtils.Shared.Cqrs.IDispatcher;

namespace EveUtils.Client.UiTests;

/// <summary>ET-295 (RO-6): publish all of a day's local activities in one go, from the day header's own button.
/// The range line's PUBLISH n LOCAL and the single-activity PUBLISH already covered by
/// <see cref="RunsServerTabsTests"/> share the same batch method — this only exercises what the batching itself
/// adds: several activities behind one confirmation, own-character filtering across all of them, and one
/// synchronise per character rather than one per activity.</summary>
public sealed class RunsPublishManyTests
{
    private static readonly DateTime StartedAtUtc = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
    private const string ServerAddress = "https://alpha.invalid";
    private const long CoupledCharacterId = 90000001;
    private const long OtherCharacterId = 90000002;

    private static readonly IReadOnlyList<Character> Crew =
        [new("Ra Vinter", (int)CoupledCharacterId), new("Vala Talon", (int)OtherCharacterId)];

    /// <summary>Only the runs of a character coupled to the target server are queued; an activity with none is
    /// skipped and counted, not treated as an error. Counter-proof: queue every local run regardless of coupling
    /// and the other character's run stops reading Local.</summary>
    [AvaloniaFact]
    public async Task PublishDay_QueuesOnlyOwnCoupledRuns_AndCountsWhatWasSkipped()
    {
        using var instance = _ConnectedInstance(out RecordingDialogService dialogs, out CountingRunSyncClient client, accepted: true);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        dialogs.OnConfirm = (_, _) => Task.FromResult(true);
        await _CoupleAsync(instance, ServerAddress, CoupledCharacterId, cancellationToken);
        Guid coupledRunId = await _SaveSiteRunAsync(instance, CoupledCharacterId, null, StartedAtUtc, cancellationToken);
        Guid otherRunId = await _SaveSiteRunAsync(instance, OtherCharacterId, null, StartedAtUtc.AddHours(1), cancellationToken);

        RunsOverviewViewModel viewModel = await _LoadAsync(instance, cancellationToken, dialogs);
        RunsDayViewModel day = Assert.Single(viewModel.Tabs[0].Days);
        Assert.Equal(2, day.LocalCount);

        await day.PublishLocalCommand.ExecuteAsync(null);

        (string title, string message) = Assert.Single(dialogs.ConfirmPrompts);
        Assert.Contains("2 activities", title);
        Assert.Contains("What you earned", message);

        Run coupledRun = await _RunAsync(instance, coupledRunId, cancellationToken);
        Assert.Equal(RunSyncState.Synced, coupledRun.SyncState);
        Assert.Equal(ServerAddress, coupledRun.SyncServerAddress);

        Run otherRun = await _RunAsync(instance, otherRunId, cancellationToken);
        Assert.Equal(RunSyncState.Local, otherRun.SyncState);
        Assert.Null(otherRun.SyncServerAddress);

        Assert.Contains("Published 1 of 2", viewModel.StatusMessage);
        Assert.Contains("had no run of a character coupled to", viewModel.StatusMessage);
    }

    /// <summary>Two activities the same character flew are one synchronise, not two — the server attributes a push
    /// to the session it came in on, so a second call in the same batch would be a second, needless session
    /// round trip. Proven through the one place that is observable from here: a grouped activity's synchronise
    /// pulls once: two activities collapsed into one call pull once, not twice.</summary>
    [AvaloniaFact]
    public async Task PublishDay_OneCharacterTwoActivities_SynchronisesOnce()
    {
        using var instance = _ConnectedInstance(out RecordingDialogService dialogs, out CountingRunSyncClient client, accepted: true);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        dialogs.OnConfirm = (_, _) => Task.FromResult(true);
        await _CoupleAsync(instance, ServerAddress, CoupledCharacterId, cancellationToken);
        await _SaveSiteRunAsync(instance, CoupledCharacterId, "grp-1", StartedAtUtc, cancellationToken);
        await _SaveSiteRunAsync(instance, CoupledCharacterId, "grp-2", StartedAtUtc.AddHours(1), cancellationToken);

        RunsOverviewViewModel viewModel = await _LoadAsync(instance, cancellationToken, dialogs);
        RunsDayViewModel day = Assert.Single(viewModel.Tabs[0].Days);
        Assert.Equal(2, day.LocalCount);

        await day.PublishLocalCommand.ExecuteAsync(null);

        Assert.Equal(1, client.PullCalls);
    }

    /// <summary>Cancelling the one confirmation queues nothing at all — not the activities already looked at before
    /// the dialog, not the ones after.</summary>
    [AvaloniaFact]
    public async Task PublishDay_Cancelled_QueuesNothing()
    {
        using var instance = _ConnectedInstance(out RecordingDialogService dialogs, out CountingRunSyncClient client, accepted: true);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        dialogs.OnConfirm = (_, _) => Task.FromResult(false);
        await _CoupleAsync(instance, ServerAddress, CoupledCharacterId, cancellationToken);
        Guid runId = await _SaveSiteRunAsync(instance, CoupledCharacterId, null, StartedAtUtc, cancellationToken);
        await _SaveSiteRunAsync(instance, CoupledCharacterId, null, StartedAtUtc.AddHours(1), cancellationToken);

        RunsOverviewViewModel viewModel = await _LoadAsync(instance, cancellationToken, dialogs);
        RunsDayViewModel day = Assert.Single(viewModel.Tabs[0].Days);

        await day.PublishLocalCommand.ExecuteAsync(null);

        Run run = await _RunAsync(instance, runId, cancellationToken);
        Assert.Equal(RunSyncState.Local, run.SyncState);
        Assert.Equal(0, client.PullCalls);
        Assert.Equal(2, day.LocalCount);
    }

    private static ICqrsDispatcher _Dispatcher(TestClientInstance instance) =>
        instance.Services.GetRequiredService<ICqrsDispatcher>();

    private static TestClientInstance _ConnectedInstance(
        out RecordingDialogService dialogs, out CountingRunSyncClient client, bool accepted)
    {
        var connector = new FakeRemoteBusConnector();
        connector.RaiseStateChanged(ServerAddress, ServerConnectionState.Connected);
        var recording = new RecordingDialogService();
        dialogs = recording;
        var syncClient = new CountingRunSyncClient(accepted);
        client = syncClient;
        return TestClientInstance.Create(services =>
        {
            services.AddSingleton<IRemoteBusConnector>(connector);
            services.AddSingleton<IServerRunSyncClient>(syncClient);
        });
    }

    private static async Task _CoupleAsync(
        TestClientInstance instance, string address, long characterId, CancellationToken cancellationToken) =>
        await instance.Services.GetRequiredService<IClientSessionStore>()
            .SaveAsync(address, new ClientSessionTokens("access", "refresh", "character", (int)characterId), cancellationToken);

    private static async Task<Guid> _SaveSiteRunAsync(
        TestClientInstance instance, long characterId, string? groupCode, DateTime startedAtUtc, CancellationToken cancellationToken)
    {
        ICqrsDispatcher dispatcher = _Dispatcher(instance);
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(characterId, ActivityKind.Site, startedAtUtc,
            1234, "Homefront", 30000142, groupCode), cancellationToken);
        await dispatcher.Send(new SaveRunCommand(started.Value, startedAtUtc.AddMinutes(15),
            startedAtUtc.AddMinutes(16), [], [], [], []), cancellationToken);
        return started.Value;
    }

    private static async Task<RunsOverviewViewModel> _LoadAsync(
        TestClientInstance instance, CancellationToken cancellationToken, RecordingDialogService? dialogs = null)
    {
        ICqrsDispatcher dispatcher = _Dispatcher(instance);
        await dispatcher.Send(new RebuildActivitySummariesCommand(), cancellationToken);
        var viewModel = new RunsOverviewViewModel(dispatcher, dialogs ?? new RecordingDialogService(), instance.Services,
            Crew, runClock: false);
        await viewModel.LoadAsync(cancellationToken);
        return viewModel;
    }

    private static async Task<Run> _RunAsync(TestClientInstance instance, Guid runId, CancellationToken cancellationToken)
    {
        await using ClientDbContext db = await instance.Services
            .GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(cancellationToken);
        return await db.Set<Run>().SingleAsync(run => run.Id == runId, cancellationToken);
    }

    /// <summary>Counts <see cref="PullAsync"/> calls — the one call a synchronise makes regardless of how many
    /// pending runs it pushes first, so it is what tells one synchronise per character apart from one per activity.</summary>
    private sealed class CountingRunSyncClient(bool accepted) : IServerRunSyncClient
    {
        public int PullCalls { get; private set; }

        public Task<(bool Accepted, string Message, DateTime? LastPushedAtUtc)> PushAsync(
            string serverAddress, RunWirePayload payload, long actingCharacterId, CancellationToken cancellationToken = default) =>
            Task.FromResult((accepted, accepted ? "Run synced." : "The server said no.",
                accepted ? (DateTime?)StartedAtUtc.AddMinutes(20) : null));

        public Task<(bool Accepted, string Message, IReadOnlyList<RunWirePayload> Runs)> PullAsync(
            string serverAddress, IReadOnlyCollection<string> groupCodes, DateTime sinceUtc, long actingCharacterId,
            CancellationToken cancellationToken = default)
        {
            PullCalls++;
            return Task.FromResult((true, "Runs synchronized.", (IReadOnlyList<RunWirePayload>)[]));
        }
    }
}
