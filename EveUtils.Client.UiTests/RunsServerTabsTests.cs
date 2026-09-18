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

/// <summary>
/// The runs screen's source tabs and the publish action. The fit browser is the pattern throughout: Local first, a
/// tab per coupled server, none at all when no server is coupled, and one target choice that is automatic with a
/// single coupled server.
/// </summary>
public sealed class RunsServerTabsTests
{
    private static readonly DateTime StartedAtUtc = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
    private const string ServerAddress = "https://alpha.invalid";
    private const string OtherServerAddress = "https://beta.invalid";

    private static readonly IReadOnlyList<Character> Crew = [new("Ra Vinter", 90000001)];

    /// <summary>A tab per coupled server and never a lone "Local": with nothing coupled the strip is a label for a
    /// choice that does not exist. Counter-proof: build the strip from a fixed list rather than from the coupled
    /// servers and the nothing-coupled row goes red on <c>HasServerTabs</c>.</summary>
    [AvaloniaTheory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task ServerTabs_FollowTheCoupledServers(int coupledServers)
    {
        using var instance = TestClientInstance.Create();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        IClientSessionStore sessions = instance.Services.GetRequiredService<IClientSessionStore>();
        foreach (string address in new[] { ServerAddress, OtherServerAddress }.Take(coupledServers))
            await sessions.SaveAsync(address, new ClientSessionTokens("access", "refresh", "Ra Vinter", 90000001), cancellationToken);

        RunsOverviewViewModel viewModel = await _LoadAsync(instance, cancellationToken);

        Assert.Equal(1 + coupledServers, viewModel.Tabs.Count);
        Assert.Equal(coupledServers > 0, viewModel.HasServerTabs);
        Assert.True(viewModel.Tabs[0].IsLocal);
    }

    /// <summary>Nothing leaves this machine before the pilot has been told what leaves with it — and the telling names
    /// the three things a run carries that a fit does not. Counter-proof: publish without asking, or ask with a
    /// "this run will be shared", and the declined row still queues or the text assertions go red.</summary>
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Publish_QueuesTheActivitysRuns_OnlyAfterAConfirmationThatNamesWhatTravels(bool confirmed)
    {
        using var instance = _ConnectedInstance(out RecordingDialogService dialogs, new InMemoryRunServer(accepts: true));
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        dialogs.OnConfirm = (_, _) => Task.FromResult(confirmed);
        await _CoupleAsync(instance, ServerAddress, cancellationToken);
        Guid runId = await _SaveSiteRunAsync(instance, cancellationToken);

        RunsOverviewViewModel viewModel = await _LoadAsync(instance, cancellationToken, dialogs);
        await _RowOf(viewModel).PublishCommand.ExecuteAsync(null);

        (string _, string message) = Assert.Single(dialogs.ConfirmPrompts);
        Assert.Contains("What you earned", message);
        Assert.Contains("The fit you flew", message);
        Assert.Contains("Where you were", message);

        Run run = await _RunAsync(instance, runId, cancellationToken);
        Assert.Equal(confirmed ? ServerAddress : null, run.SyncServerAddress);
        Assert.Equal(confirmed ? RunSyncState.Synced : RunSyncState.Local, run.SyncState);
    }

    /// <summary>A server that refuses the push leaves the runs queued for it and says why: they are still meant for
    /// that server, so the next attempt retries them instead of the pilot having to notice they never arrived.
    /// Counter-proof: report nothing on a rejection and the status assertion goes red — the silent failure this
    /// mirrors the fit share's single report to avoid.</summary>
    [AvaloniaFact]
    public async Task Publish_RejectedByTheServer_KeepsTheRunsQueuedAndSaysWhy()
    {
        using var instance = _ConnectedInstance(out RecordingDialogService dialogs, new InMemoryRunServer(accepts: false));
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        dialogs.OnConfirm = (_, _) => Task.FromResult(true);
        await _CoupleAsync(instance, ServerAddress, cancellationToken);
        Guid runId = await _SaveSiteRunAsync(instance, cancellationToken);

        RunsOverviewViewModel viewModel = await _LoadAsync(instance, cancellationToken, dialogs);
        await _RowOf(viewModel).PublishCommand.ExecuteAsync(null);

        Run run = await _RunAsync(instance, runId, cancellationToken);
        Assert.Equal(RunSyncState.Pending, run.SyncState);
        Assert.Equal(ServerAddress, run.SyncServerAddress);
        Assert.Contains("The server said no.", viewModel.StatusMessage);
    }

    /// <summary>A server tab is a read of that server (ET-311), not a filter over the local database: what the server
    /// holds is on its tab after a publish it accepted, and nothing is there after one it refused — the run is queued
    /// on Local, where it still shows. Counter-proof: file rows by the address their local runs carry and the refused
    /// row lands on the server tab, since a queued run already carries it.</summary>
    [AvaloniaTheory]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    public async Task ServerTab_HoldsOnlyTheActivitiesPublishedToIt(bool confirmed, bool serverAccepts, bool onTheServerTab)
    {
        using var instance = _ConnectedInstance(out RecordingDialogService dialogs, new InMemoryRunServer(serverAccepts));
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        dialogs.OnConfirm = (_, _) => Task.FromResult(confirmed);
        await _CoupleAsync(instance, ServerAddress, cancellationToken);
        await _SaveSiteRunAsync(instance, cancellationToken);

        RunsOverviewViewModel viewModel = await _LoadAsync(instance, cancellationToken, dialogs);
        await _RowOf(viewModel).PublishCommand.ExecuteAsync(null);
        RunsTabViewModel serverTab = viewModel.Tabs.Single(tab => tab.ServerAddress == ServerAddress);

        Assert.Single(viewModel.Tabs[0].Days);
        Assert.Equal(onTheServerTab ? 1 : 0, serverTab.Days.Count);
    }

    /// <summary>The acceptance of ET-311: a clean installation whose character has runs on the server shows them on
    /// that server's tab, with nothing in the local database and Local empty. Red on main, where the server tab was
    /// the local rows filtered by address — and there were none. The row is read-only there: the pane's OPEN DETAIL
    /// is for a local activity a pilot can correct (ET-214/215).</summary>
    [AvaloniaFact]
    public async Task ServerTab_CleanClient_ShowsWhatTheServerHolds_AndLocalStaysEmpty()
    {
        var server = new InMemoryRunServer(accepts: true);
        server.Hold(ServerAddress, 90000001);
        using var instance = _ConnectedInstance(out RecordingDialogService dialogs, server);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await _CoupleAsync(instance, ServerAddress, cancellationToken);

        RunsOverviewViewModel viewModel = await _LoadAsync(instance, cancellationToken, dialogs);
        RunsTabViewModel serverTab = viewModel.Tabs.Single(tab => tab.ServerAddress == ServerAddress);

        Assert.Empty(viewModel.Tabs[0].Days);
        ActivityOverviewRowViewModel row = Assert.Single(Assert.Single(serverTab.Days).Rows);
        Assert.False(row.CanOpenDetail);
        Assert.False(row.IsLocal);
    }

    private static ICqrsDispatcher _Dispatcher(TestClientInstance instance) =>
        instance.Services.GetRequiredService<ICqrsDispatcher>();

    /// <summary>An instance whose server answers: connected on the bus — the server as a whole for a publish, and
    /// Ra Vinter's own link for a server tab's read (ET-311) — and a run-sync client that accepts or refuses on
    /// demand. No network is touched — only the decision branches around it.</summary>
    private static TestClientInstance _ConnectedInstance(out RecordingDialogService dialogs, IServerRunSyncClient server)
    {
        var connector = new FakeRemoteBusConnector();
        connector.RaiseStateChanged(ServerAddress, ServerConnectionState.Connected);
        connector.RaiseCharacterStateChanged(ServerAddress, 90000001, ServerConnectionState.Connected);
        var recording = new RecordingDialogService();
        dialogs = recording;
        return TestClientInstance.Create(services =>
        {
            services.AddSingleton<IRemoteBusConnector>(connector);
            services.AddSingleton(server);
        });
    }

    private static async Task _CoupleAsync(TestClientInstance instance, string address, CancellationToken cancellationToken) =>
        await instance.Services.GetRequiredService<IClientSessionStore>()
            .SaveAsync(address, new ClientSessionTokens("access", "refresh", "Ra Vinter", 90000001), cancellationToken);

    private static async Task<Guid> _SaveSiteRunAsync(TestClientInstance instance, CancellationToken cancellationToken)
    {
        ICqrsDispatcher dispatcher = _Dispatcher(instance);
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Site, StartedAtUtc,
            1234, "Homefront", 30000142), cancellationToken);
        await dispatcher.Send(new SaveRunCommand(started.Value, StartedAtUtc.AddMinutes(15),
            StartedAtUtc.AddMinutes(16), [], [], [], []), cancellationToken);
        return started.Value;
    }

    private static async Task<RunsOverviewViewModel> _LoadAsync(
        TestClientInstance instance, CancellationToken cancellationToken, RecordingDialogService? dialogs = null)
    {
        ICqrsDispatcher dispatcher = _Dispatcher(instance);
        await dispatcher.Send(new RebuildActivitySummariesCommand(), cancellationToken);
        // No lane clock: nothing closes this view-model, so a DispatcherTimer would tick on for the rest of the run.
        var viewModel = new RunsOverviewViewModel(dispatcher, dialogs ?? new RecordingDialogService(), instance.Services,
            Crew, runClock: false);
        await viewModel.LoadAsync(cancellationToken);
        return viewModel;
    }

    private static ActivityOverviewRowViewModel _RowOf(RunsOverviewViewModel viewModel) =>
        Assert.Single(Assert.Single(viewModel.Tabs[0].Days).Rows);

    private static async Task<Run> _RunAsync(TestClientInstance instance, Guid runId, CancellationToken cancellationToken)
    {
        await using ClientDbContext db = await instance.Services
            .GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(cancellationToken);
        return await db.Set<Run>().SingleAsync(run => run.Id == runId, cancellationToken);
    }

    /// <summary>The server as far as the client can tell: it takes or refuses a push, keeps what it took, and answers
    /// a server tab's read from that — per address, so a run pushed to one server is never on another's tab.</summary>
    private sealed class InMemoryRunServer(bool accepts) : IServerRunSyncClient
    {
        private readonly List<(string Server, RunWireData Run)> _held = [];

        /// <summary>A saved run of <paramref name="characterId"/> the server holds before this client ever saw it.</summary>
        public void Hold(string serverAddress, long characterId) =>
            _held.Add((serverAddress, RunWireData.FromEntity(new Run
            {
                Id = Guid.CreateVersion7(), CharacterId = characterId, ActivityKind = ActivityKind.Site, State = RunState.Saved,
                StartedAtUtc = StartedAtUtc, StoppedAtUtc = StartedAtUtc.AddMinutes(15), SavedAtUtc = StartedAtUtc.AddMinutes(16),
                SiteTypeId = 1234, SiteName = "Homefront", SolarSystemId = 30000142, SyncState = RunSyncState.Synced, Revision = 2
            })));

        public Task<(bool Accepted, string Message, DateTime? LastPushedAtUtc)> PushAsync(
            string serverAddress, RunWirePayload payload, long actingCharacterId, CancellationToken cancellationToken = default)
        {
            if (accepts)
                _held.Add((serverAddress, payload.Run));
            return Task.FromResult((accepts, accepts ? "Run synced." : "The server said no.",
                accepts ? (DateTime?)StartedAtUtc.AddMinutes(20) : null));
        }

        public Task<(bool Accepted, string Message, IReadOnlyList<RunWirePayload> Runs)> PullAsync(
            string serverAddress, IReadOnlyCollection<string> groupCodes, DateTime sinceUtc, long actingCharacterId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult((true, "Runs synchronized.", (IReadOnlyList<RunWirePayload>)[]));

        public Task<(bool Accepted, string Message, IReadOnlyList<RunWirePayload> Runs)> ListPublishedAsync(
            string serverAddress, DateTime fromUtc, DateTime toUtc, long actingCharacterId, CancellationToken cancellationToken = default) =>
            Task.FromResult((true, "Runs synchronized.", (IReadOnlyList<RunWirePayload>)[.. _held
                .Where(held => held.Server == serverAddress && held.Run.StartedAtUtc >= fromUtc && held.Run.StartedAtUtc < toUtc)
                .Select(held => new RunWirePayload
                {
                    Run = held.Run, SentAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                })]));
    }
}
