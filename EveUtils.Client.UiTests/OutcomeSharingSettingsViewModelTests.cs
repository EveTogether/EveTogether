using Avalonia.Headless.XUnit;
using EveUtils.Client.Messaging;
using EveUtils.Client.Notifications;
using EveUtils.Client.Runs;
using EveUtils.Client.Transport;
using EveUtils.Client.ViewModels;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.RunOutcomes.Dtos;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Earnings;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Transport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>ET-515: the EARNINGS STATS block of the settings — one switch per coupled server, off until the pilot
/// turns it on, applied only through what SAVE collects.</summary>
public sealed class OutcomeSharingSettingsViewModelTests
{
    private const long Pilot = 90000001;
    private const string Server = "https://krah.invalid";
    private const string OtherServer = "https://alliance.invalid";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Every coupled server gets its own row, off, saying "Not shared" until the pilot chose; nothing changes
    /// until a switch moves. Counter-proof: default the switches on and the first save already carries a change.</summary>
    [AvaloniaFact]
    public async Task Load_ListsEveryCoupledServer_Off_AndChangesNothing()
    {
        using TestClientInstance instance = _Instance();
        await _CoupleAsync(instance, Server, OtherServer);

        OutcomeSharingSettingsViewModel viewModel = await _LoadAsync(instance);

        Assert.Equal([OtherServer, Server], viewModel.Servers.Select(server => server.Address));
        Assert.All(viewModel.Servers, server =>
        {
            Assert.False(server.IsShared);
            Assert.Equal("Not shared", server.StatusText);
        });
        Assert.Empty(viewModel.Changes());
        Assert.True(viewModel.HasServers);
    }

    /// <summary>No coupled server: the block says so instead of showing an empty list.</summary>
    [AvaloniaFact]
    public async Task Load_WithoutACoupledServer_HasNoServers()
    {
        using TestClientInstance instance = _Instance();

        OutcomeSharingSettingsViewModel viewModel = await _LoadAsync(instance);

        Assert.True(viewModel.HasNoServers);
        Assert.Empty(viewModel.Changes());
    }

    /// <summary>Turning a server on offers the earlier runs (with their count) and the change carries both the choice and
    /// whether they go along; declining them is carried too. Counter-proof: without the offer the count never shows.</summary>
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TurningOn_OffersTheEarlierRuns_AndTheChangeCarriesTheAnswer(bool keepBackfill)
    {
        using TestClientInstance instance = _Instance();
        await _CoupleAsync(instance, Server);
        await _SaveRunAsync(instance, DateTime.UtcNow.AddDays(-20));
        await _SaveRunAsync(instance, DateTime.UtcNow.AddDays(-10));
        OutcomeSharingSettingsViewModel viewModel = await _LoadAsync(instance);
        OutcomeSharingServerViewModel row = Assert.Single(viewModel.Servers);

        row.IsShared = true;
        await _WaitForAsync(() => row.BackfillCount > 0);
        row.Backfill = keepBackfill;

        Assert.True(row.OffersBackfill);
        Assert.Equal("Also share my runs from the last 90 days (2)", row.BackfillText);
        Assert.Equal("Sharing starts when you save.", row.StatusText);
        Assert.Equal([new OutcomeSharingChange(Server, OutcomeSharingChoice.Shared, keepBackfill)], viewModel.Changes());
    }

    /// <summary>Turning it on and off again before SAVE is no change at all.</summary>
    [AvaloniaFact]
    public async Task TurningOnAndOffAgain_IsNoChange()
    {
        using TestClientInstance instance = _Instance();
        await _CoupleAsync(instance, Server);
        OutcomeSharingSettingsViewModel viewModel = await _LoadAsync(instance);
        OutcomeSharingServerViewModel row = Assert.Single(viewModel.Servers);

        row.IsShared = true;
        row.IsShared = false;

        Assert.Empty(viewModel.Changes());
        Assert.False(row.OffersBackfill);
    }

    /// <summary>A server already shared with reads as shared, and turning it off says what that does — everything
    /// shared is removed — and carries the opt-out. Counter-proof: read the state wrongly and the switch starts off.</summary>
    [AvaloniaFact]
    public async Task TurningOffASharedServer_SaysItRemovesWhatWasShared_AndCarriesTheOptOut()
    {
        using TestClientInstance instance = _Instance();
        await _CoupleAsync(instance, Server);
        await instance.Services.GetRequiredService<RunOutcomeSharingChoices>().SetAsync(Server, OutcomeSharingChoice.Shared, Token);
        OutcomeSharingSettingsViewModel viewModel = await _LoadAsync(instance);
        OutcomeSharingServerViewModel row = Assert.Single(viewModel.Servers);
        Assert.True(row.IsShared);

        row.IsShared = false;

        Assert.Equal("Everything you shared is removed when you save.", row.StatusText);
        Assert.Equal([new OutcomeSharingChange(Server, OutcomeSharingChoice.NotShared, false)], viewModel.Changes());
    }

    /// <summary>The status line reads like the mockup: how many, and when the last one went — and what is waiting.</summary>
    [AvaloniaTheory]
    [InlineData(OutcomeSharingChoice.NotShared, 0, 0, "Not shared")]
    [InlineData(OutcomeSharingChoice.Shared, 37, 0, "Shared: 37 outcomes · last 2 min ago")]
    [InlineData(OutcomeSharingChoice.Shared, 1, 3, "Shared: 1 outcome · last 2 min ago · 3 waiting")]
    public void SharedLine_ReadsHowManyAndWhen(OutcomeSharingChoice choice, int shared, int waiting, string expected)
    {
        DateTime now = new(2026, 10, 19, 20, 0, 0, DateTimeKind.Utc);
        var status = new OutcomeSharingStatus(Server, choice, shared, shared > 0 ? now.AddMinutes(-2) : null, waiting, null);

        Assert.Equal(expected, OutcomeSharingServerViewModel.SharedLine(status, now));
    }

    [AvaloniaTheory]
    [InlineData(20, "just now")]
    [InlineData(5 * 60, "5 min ago")]
    [InlineData(3 * 3600, "3 h ago")]
    [InlineData(4 * 86400, "4 d ago")]
    public void Age_ReadsInTheLargestUnit(int seconds, string expected) =>
        Assert.Equal(expected, OutcomeSharingServerViewModel.Age(TimeSpan.FromSeconds(seconds)));

    /// <summary>Why it is not going through is shown, not hidden in a log.</summary>
    [AvaloniaFact]
    public void Problem_IsShownWhenThereIsOne()
    {
        var status = new OutcomeSharingStatus(Server, OutcomeSharingChoice.Shared, 0, null, 1, "The server has switched earnings sharing off.");
        using TestClientInstance instance = _Instance();
        var row = new OutcomeSharingServerViewModel(instance.Services.GetRequiredService<RunOutcomePublisher>(), Server, "Krah Server",
            status, DateTime.UtcNow);

        Assert.True(row.HasProblem);
        Assert.Equal("The server has switched earnings sharing off.", row.Problem);
    }

    private static TestClientInstance _Instance() => TestClientInstance.Create(services =>
    {
        services.AddSingleton<IRemoteBusConnector>(new FakeRemoteBusConnector());
        services.AddSingleton<IServerRunOutcomeClient>(new IdleOutcomeServer());
        services.AddSingleton<IToastService>(new RecordingToastService());
    });

    private static async Task _CoupleAsync(TestClientInstance instance, params string[] servers)
    {
        IClientSessionStore sessions = instance.Services.GetRequiredService<IClientSessionStore>();
        foreach (string server in servers)
            await sessions.SaveAsync(server, new ClientSessionTokens("access", "refresh", "Jithran", (int)Pilot), Token);
    }

    private static Task<OutcomeSharingSettingsViewModel> _LoadAsync(TestClientInstance instance) =>
        OutcomeSharingSettingsViewModel.LoadAsync(instance.Services.GetRequiredService<RunOutcomePublisher>(),
            instance.Services.GetRequiredService<IClientSessionStore>(), instance.Services.GetRequiredService<IServerRegistry>(),
            DateTime.UtcNow, Token);

    private static async Task _SaveRunAsync(TestClientInstance instance, DateTime startedAtUtc)
    {
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(Pilot, ActivityKind.Site, startedAtUtc, 2311, "Angel Sanctum",
            30000142), Token);
        Result saved = await dispatcher.Send(new SaveRunCommand(started.Value, startedAtUtc.AddMinutes(15), startedAtUtc.AddMinutes(16),
        [
            new RunLootCaptureInput
            {
                CapturedAtUtc = startedAtUtc.AddMinutes(10), Source = LootCaptureSource.Clipboard, ContentHash = started.Value.ToString(),
                Entries = [new RunLootEntryInput { ItemTypeId = 34, Name = "Tritanium", Quantity = 10, LootKind = LootKind.Gained }]
            }
        ], [], [], []), Token);
        Assert.True(saved.IsSuccess);
        await instance.Services.GetRequiredService<RunOutcomePublisher>().WhenIdleAsync();
    }

    private static async Task _WaitForAsync(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 200 && !condition(); attempt++)
            await Task.Delay(25, Token);
    }

    private sealed class IdleOutcomeServer : IServerRunOutcomeClient
    {
        public Task<OutcomeCallResult> ShareAsync(string serverAddress, long characterId, string activityRef, long revision,
            RunOutcomeShareDto? outcome, CancellationToken cancellationToken = default) =>
            Task.FromResult(OutcomeCallResult.Transient("not used"));

        public Task<OutcomeCallResult> WithdrawAsync(string serverAddress, long characterId, CancellationToken cancellationToken = default) =>
            Task.FromResult(OutcomeCallResult.Transient("not used"));

        public Task<OutcomeReadResult<ActivityOutcomesDto>> GetOutcomesAsync(string serverAddress, long characterId,
            ActivityStatsKey key, DateOnly since, CancellationToken cancellationToken = default) =>
            Task.FromResult(OutcomeReadResult<ActivityOutcomesDto>.Transient("not used"));

        public Task<OutcomeReadResult<ActivityOutcomeLootDto>> GetOutcomeLootAsync(string serverAddress, long characterId,
            ActivityStatsKey key, DateOnly since, CancellationToken cancellationToken = default) =>
            Task.FromResult(OutcomeReadResult<ActivityOutcomeLootDto>.Transient("not used"));
    }
}
