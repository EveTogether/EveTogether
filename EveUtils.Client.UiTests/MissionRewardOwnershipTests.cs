using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using EveUtils.Client.Clipboard;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Notifications;
using EveUtils.Client.Platform;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Shared.Data;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Market.Services;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Queries;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ICqrsDispatcher = EveUtils.Shared.Cqrs.IDispatcher;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-260: a mission flown with more than one own toon (one shooter, one salvage alt) used to write the identical
/// reward parameters — ISK, BonusIsk, LP — onto every one of that group's runs, because every one of them received
/// the same <c>StartRunCommand.Parameters</c> list. EVE only ever pays the character who accepted the mission, so
/// the source fix writes them onto that one run only; every display that used to sum them per run instead reads
/// (or is repaired to read) that single copy.
/// </summary>
public sealed class MissionRewardOwnershipTests
{
    private static readonly DateTime StartedAtUtc = new(2026, 9, 12, 12, 0, 0, DateTimeKind.Utc);

    // ── The source fix: ClipboardMissionOffer writes the reward onto one run only ─────────────────────

    /// <summary>Jithran's own report: two own toons on one fleet, one mission activated once, both runs ended up
    /// with the reward. The picker's first pick (the "acting" pilot) is deliberately NOT the character who copied
    /// the clipboard here — proving the reward follows <c>ClipboardCapture.CopiedByCharacter</c>, not picking order.
    /// Counter-proof: before ET-260, both runs' <c>RunParameter</c> rows come back non-empty.</summary>
    [AvaloniaFact]
    public async Task TwoOwnToons_RewardLandsOnlyOnTheCharacterWhoseClipboardCopyStartedTheMission()
    {
        using var env = await Env.StartAsync(copiedByCharacter: "Second Pilot");
        await env.AddCharacterAsync("First Pilot", 90000001);
        await env.AddCharacterAsync("Second Pilot", 90000002);
        env.Dialogs.OnPickCharacters = (_, _) => Task.FromResult<IReadOnlyList<int>?>([90000001, 90000002]);

        env.Copy(_MissionCapture("1.000.000 ISK"));
        await Env.WaitUntil(() => env.Dialogs.ShownActivityWindows.Count > 0);
        ActivityWindowViewModel opened = Assert.Single(env.Dialogs.ShownActivityWindows);
        await opened.LoadAsync();
        List<Run> runs = await Env.WaitForRunningMissionsAsync(env, expectedCount: 2);

        Run firstPilotRun = Assert.Single(runs, run => run.CharacterId == 90000001);
        Run secondPilotRun = Assert.Single(runs, run => run.CharacterId == 90000002);
        await using ClientDbContext db = await env.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync();
        List<RunParameter> firstPilotParameters = await db.Set<RunParameter>().Where(p => p.RunId == firstPilotRun.Id).ToListAsync();
        List<RunParameter> secondPilotParameters = await db.Set<RunParameter>().Where(p => p.RunId == secondPilotRun.Id).ToListAsync();

        Assert.Empty(firstPilotParameters); // the salvage alt, first picked, gets nothing
        Assert.Contains(secondPilotParameters, p => p.ParameterKey == RunParameterKey.Isk && p.Amount == 1_000_000m);
    }

    /// <summary>A solo mission is unaffected (ET-260 AC-3): with nobody else to withhold the reward from, the one
    /// character who started it still gets it, exactly as before.</summary>
    [AvaloniaFact]
    public async Task OneOwnToon_StillGetsTheReward()
    {
        using var env = await Env.StartAsync();
        await env.AddCharacterAsync("Solo Pilot", 90000001);

        env.Copy(_MissionCapture("1.000.000 ISK"));
        await Env.WaitUntil(() => env.Dialogs.ShownActivityWindows.Count > 0);
        await Assert.Single(env.Dialogs.ShownActivityWindows).LoadAsync();
        List<Run> runs = await Env.WaitForRunningMissionsAsync(env, expectedCount: 1);

        await using ClientDbContext db = await env.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync();
        List<RunParameter> parameters = await db.Set<RunParameter>().Where(p => p.RunId == runs[0].Id).ToListAsync();
        Assert.Contains(parameters, p => p.ParameterKey == RunParameterKey.Isk && p.Amount == 1_000_000m);
    }

    private static string _MissionCapture(string rewardLine) =>
        "Aralin Jick Objectives\r\n" +
        "The following objectives must be completed to finish the mission:\r\n" +
        "\r\n" +
        "Report to Aralin Jick\r\n" +
        " \tAgent Location\t0,6 Nishah VII - Moon 5 - Kor-Azor Family Treasury\r\n" +
        "\r\n" +
        "Rewards\r\n" +
        "The following rewards will be yours if you complete this mission:\r\n" +
        " \t" + rewardLine;

    // ── The display fixes: no reward display sums duplicated parameters per run ──────────────────────

    /// <summary>The overview's reward chips (Jithran's own measured symptom: double ISK/LP) used to sum every
    /// matching <c>RunParameter</c> across the whole activity with no regard for which run it came from — a mission
    /// saved before ET-260's source fix (or its one-time dedupe) still had the identical line on both runs.
    /// Counter-proof: remove the <c>DistinctBy</c> in <c>GetActivityOverviewQueryHandler._ToDto</c> and this goes red
    /// on 2,000,000 instead of 1,000,000.</summary>
    [AvaloniaFact]
    public async Task Overview_RewardChip_DoesNotDoubleAPreExistingDuplicate()
    {
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = instance.Services.GetRequiredService<ICqrsDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        const string groupCode = "HF-TEST1";
        await _StartAndSaveMissionRunAsync(dispatcher, 90000001, groupCode, "Shooter", 1_000_000m, cancellationToken);
        await _StartAndSaveMissionRunAsync(dispatcher, 90000002, groupCode, "Salvager", 1_000_000m, cancellationToken);

        Result<IReadOnlyList<ActivityOverviewRowDto>> overview = await dispatcher.Query(new GetActivityOverviewQuery(), cancellationToken);
        ActivityOverviewRowDto row = Assert.Single(overview.Value!);

        ActivityRewardDto reward = Assert.Single(row.Rewards, r => r.ParameterKey == RunParameterKey.Isk);
        Assert.Equal(1_000_000m, reward.Amount);
    }

    /// <summary>The MISSION detail section's own reward rows (one of the candidates the ticket named to measure)
    /// used to iterate every run's parameters with no dedupe — the same pre-existing duplicate as above read as two
    /// identical reward rows instead of one, and named no character in particular for either.</summary>
    [AvaloniaFact]
    public async Task MissionDetail_DoesNotDoubleAPreExistingDuplicate_AndNamesWhoItBelongsTo()
    {
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = instance.Services.GetRequiredService<ICqrsDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        const string groupCode = "HF-TEST2";
        await _StartAndSaveMissionRunAsync(dispatcher, 90000001, groupCode, "Shooter", 1_000_000m, cancellationToken);
        await _StartAndSaveMissionRunAsync(dispatcher, 90000002, groupCode, "Salvager", 1_000_000m, cancellationToken);
        Result<IReadOnlyList<ActivityOverviewRowDto>> overview = await dispatcher.Query(new GetActivityOverviewQuery(), cancellationToken);
        ActivityOverviewRowDto row = Assert.Single(overview.Value!);

        var viewModel = new ActivityDetailViewModel(dispatcher, row.ActivitySummaryId,
            instance.Services.GetRequiredService<IAppraisalProvider>());
        await viewModel.LoadAsync(cancellationToken);

        ActivityRewardRowViewModel rewardRow = Assert.Single(viewModel.Mission().RewardRows, r => r.Label == "ISK");
        Assert.Equal("1,000,000", rewardRow.ValueText);
        Assert.True(viewModel.Mission().IsRewardOwnerShown);
        Assert.Equal("Shooter", viewModel.Mission().RewardOwnerText); // whichever run the parameters landed on
    }

    /// <summary>The post-fix, non-duplicated case: only the owner's run carries the parameter at all — the normal
    /// shape a mission saves in from now on. The other own toon's run has none, and the section still names the
    /// right character rather than reading ambiguous.</summary>
    [AvaloniaFact]
    public async Task MissionDetail_WithOnlyOneRunCarryingTheReward_NamesThatCharacter()
    {
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = instance.Services.GetRequiredService<ICqrsDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        const string groupCode = "HF-TEST3";
        await _StartAndSaveMissionRunAsync(dispatcher, 90000001, groupCode, "Shooter", 1_000_000m, cancellationToken);
        // The salvage alt's own run: same group, no reward parameters at all — the shape ET-260's source fix produces.
        Result<Guid> altStarted = await dispatcher.Send(new StartRunCommand(90000002, ActivityKind.Mission, StartedAtUtc,
            4022, "Some Mission", 30000142, groupCode, SiteTypeSource: SiteTypeSource.Mission,
            CharacterNameSnapshot: "Salvager"), cancellationToken);
        await dispatcher.Send(new SaveRunCommand(altStarted.Value, StartedAtUtc.AddMinutes(15), StartedAtUtc.AddMinutes(16),
            [], [], [], []), cancellationToken);
        Result<IReadOnlyList<ActivityOverviewRowDto>> overview = await dispatcher.Query(new GetActivityOverviewQuery(), cancellationToken);
        ActivityOverviewRowDto row = Assert.Single(overview.Value!);

        var viewModel = new ActivityDetailViewModel(dispatcher, row.ActivitySummaryId,
            instance.Services.GetRequiredService<IAppraisalProvider>());
        await viewModel.LoadAsync(cancellationToken);

        Assert.True(viewModel.Mission().IsRewardOwnerShown);
        Assert.Equal("Shooter", viewModel.Mission().RewardOwnerText);
    }

    private static async Task _StartAndSaveMissionRunAsync(ICqrsDispatcher dispatcher, long characterId,
        string groupCode, string characterName, decimal isk, CancellationToken cancellationToken)
    {
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(characterId, ActivityKind.Mission, StartedAtUtc,
            4022, "Some Mission", 30000142, groupCode, SiteTypeSource: SiteTypeSource.Mission,
            CharacterNameSnapshot: characterName), cancellationToken);
        await dispatcher.Send(new SaveRunCommand(started.Value, StartedAtUtc.AddMinutes(15), StartedAtUtc.AddMinutes(16),
            [], [], [],
            [new RunParameterInput { ParameterKey = RunParameterKey.Isk, TypedValue = isk.ToString(), Amount = isk, ObservedAtUtc = StartedAtUtc }]),
            cancellationToken);
    }

    private sealed class Env : IDisposable
    {
        private readonly TestClientInstance _instance;
        private readonly ClipboardWatchService _watch;
        private readonly ClipboardMissionOffer _offer;
        private readonly FakeClipboardChangeSource _source;

        public RecordingToastService Toasts { get; } = new();

        public RecordingDialogService Dialogs { get; } = new();

        public IServiceProvider Services => _instance.Services;

        private Env(TestClientInstance instance, ClipboardWatchService watch, FakeClipboardChangeSource source)
        {
            _instance = instance;
            _watch = watch;
            _source = source;
            _offer = new ClipboardMissionOffer(watch, Toasts, new FakeSdeAccessor(), Dialogs, instance.Services);
        }

        public static async Task<Env> StartAsync(string? copiedByCharacter = null)
        {
            var source = new FakeClipboardChangeSource();
            var instance = TestClientInstance.Create();
            var watch = new ClipboardWatchService(new RecordingDialogService(), instance.Services,
                NullLogger<ClipboardWatchService>.Instance, source, new FakeForegroundReader(copiedByCharacter));
            var env = new Env(instance, watch, source);
            await watch.SetEnabledAsync(true);
            return env;
        }

        public async Task AddCharacterAsync(string name, int esiCharacterId) =>
            await Services.GetRequiredService<ICharacterRegistry>().AddOrUpdateAsync(new Character(name, esiCharacterId));

        public void Copy(string text)
        {
            _source.ClipboardText = text;
            _source.RaiseChanged();
            Dispatcher.UIThread.RunJobs();
        }

        public static async Task WaitUntil(Func<bool> condition, int timeoutMs = 5000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                Dispatcher.UIThread.RunJobs();
                if (condition())
                    return;

                await Task.Delay(25);
            }

            throw new Xunit.Sdk.XunitException("condition not met within the timeout");
        }

        /// <summary>Polls without blocking the UI-thread synchronization context the headless tests run on.</summary>
        public static async Task<List<Run>> WaitForRunningMissionsAsync(Env env, int expectedCount, int timeoutMs = 5000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                Dispatcher.UIThread.RunJobs();
                await using ClientDbContext db = await env.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync();
                List<Run> runs = await db.Set<Run>().AsNoTracking()
                    .Where(run => run.ActivityKind == ActivityKind.Mission && run.State == RunState.Running)
                    .ToListAsync();
                if (runs.Count == expectedCount)
                    return runs;

                await Task.Delay(25);
            }

            throw new Xunit.Sdk.XunitException("the expected number of running mission runs never appeared within the timeout");
        }

        public void Dispose()
        {
            _offer.Dispose();
            _watch.Dispose();
            _instance.Dispose();
        }
    }

    private sealed class FakeClipboardChangeSource : IClipboardChangeSource
    {
        public string? ClipboardText { get; set; }

        public bool IsSupported => true;

        public event Action? Changed;

        public event Action? SupportChanged
        {
            add { }
            remove { }
        }

        public void Start()
        {
        }

        public void Stop()
        {
        }

        public void Dispose()
        {
        }

        public Task<string?> ReadTextAsync() => Task.FromResult(ClipboardText);

        public void RaiseChanged() => Changed?.Invoke();
    }

    private sealed class FakeForegroundReader(string? character) : IForegroundEveClientReader
    {
        public string? CharacterAtForegroundWindow() => character;

        public ForegroundWindowSnapshot DescribeForegroundWindow() => ForegroundWindowSnapshot.Unknown;
    }
}
