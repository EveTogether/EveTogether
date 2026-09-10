using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using EveUtils.Client.Clipboard;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Notifications;
using EveUtils.Client.Runs;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Shared.Data;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Sde.Dtos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// <see cref="ClipboardMissionOffer"/> (ET-172 sub 4): a copied mission Objectives block starts its run the same
/// way one fully-scanned combat site does (ET-158) — agent, level and system resolved from the SDE import
/// (ET-173), never parsed from the clipboard text, and rewards written onto the existing <see cref="RunParameter"/>
/// shapes.
/// </summary>
public sealed class ClipboardMissionOfferTests
{
    /// <summary>Raymond's clipboard capture, byte for byte.</summary>
    private static string MeasuredMissionBlock => _Fixture("mission-aralin-jick.txt");

    private static string SpaceSeparatedMissionBlock => MeasuredMissionBlock.Replace("\t", "  ").Replace("  \n", "\n").TrimEnd();

    // Raymond reported the number forms; no clipboard capture of them is retained.
    public static TheoryData<string, decimal> MeasuredMissionWhitespaceVariants => new()
    {
        { MeasuredMissionBlock, 1_000_000m },
        { SpaceSeparatedMissionBlock, 1_000_000m },
        { SpaceSeparatedMissionBlock.Replace("1.000.000", "1,000,000"), 1_000_000m },
        { SpaceSeparatedMissionBlock.Replace("1.000.000", "1 000 000"), 1_000_000m },
        { SpaceSeparatedMissionBlock.Replace("1.000.000", "1\u00A0000\u00A0000"), 1_000_000m },
        { SpaceSeparatedMissionBlock.Replace("1.000.000", "1\u202F000\u202F000"), 1_000_000m },
        { SpaceSeparatedMissionBlock.Replace("1.000.000", "5 000"), 5_000m }
    };

    // ET-172 sub 4 AC-1..AC-5, AC-7: the SDE facts measured against build 3492266 in the epic's own grooming —
    // Aralin Jick is agent 3019407, level 4, an EpicArcAgent, at Nishah (system 30005040).
    private static SdeAgent AralinJick => new(3019407, "Aralin Jick", Level: 4, AgentTypeId: 10,
        AgentTypeName: "EpicArcAgent", DivisionId: 1, IsLocator: false, CorporationId: 1000089,
        LocationId: 60008689, SolarSystemId: 30005040, SolarSystemName: "Nishah");

    // AC-1 countercheck: the same block, closed window and already-open window, both land a running run — ET-158
    // showed that repairing only one of the two routes leaves the other silently broken.
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AMissionCapture_StartsARun_ClosedOrAlreadyOpen(bool windowAlreadyOpen)
    {
        using var env = await Env.StartAsync();
        env.Sde.AddAgent(AralinJick);
        await env.AddCharacterAsync();

        ActivityWindowViewModel? open = null;
        if (windowAlreadyOpen)
        {
            open = new ActivityWindowViewModel(ActivityKind.Mission, env.Services);
            env.Dialogs.ShowActivityWindow(open, RunWindowOpenTrigger.LocalUser);
        }

        env.Copy(MeasuredMissionBlock);
        Run run = await WaitForRunningMissionAsync(env);
        Assert.Equal(ActivityKind.Mission, run.ActivityKind);                 // AC-2
        Assert.Equal(3019407, run.AgentId);                                   // AC-3
        Assert.Equal(4, run.MissionLevel);                                    // AC-3
        Assert.Equal(30005040, run.SolarSystemId);                            // AC-4 — not the "0,6" in the text
        Assert.Equal(SiteTypeSource.Mission, run.SiteTypeSource);             // AC-5
        if (open is not null)
        {
            await open.LastMission;
            Assert.Equal("Level 4", open.MissionLevelText);
        }

        // AC-7: the arc-ness is not stored redundantly — it is read back through the very agent id the run carries.
        SdeAgent? agent = env.Sde.GetAgent(run.AgentId!.Value);
        Assert.Equal("EpicArcAgent", agent?.AgentTypeName);
    }

    // AC-6 countercheck: one test over the full reward block, all four RunParameterKey shapes it can produce. No real
    // capture carries a Loyalty Points or Item line, so this block is built rather than measured — its only job is
    // to prove the four reward shapes each land on the row ET-137 already defined for them.
    [AvaloniaFact]
    public async Task AMissionCapture_WritesEveryRewardShape_OntoTheExistingRunParameterRows()
    {
        using var env = await Env.StartAsync();
        env.Sde.AddAgent(AralinJick);
        env.Sde.Add(34, "Antimatter Charge S", groupId: 372, categoryId: 8);
        await env.AddCharacterAsync();

        env.Copy(
            "Aralin Jick Objectives\r\n" +
            "The following objectives must be completed to finish the mission:\r\n" +
            "\r\n" +
            "Report to Aralin Jick\r\n" +
            " \tAgent Location\t0,6 Nishah VII - Moon 5 - Kor-Azor Family Treasury\r\n" +
            "\r\n" +
            "Rewards\r\n" +
            "The following rewards will be yours if you complete this mission:\r\n" +
            " \t1.000.000 ISK\r\n" +
            " \t5000 Loyalty Points\r\n" +
            " \t3 × Antimatter Charge S\r\n" +
            "\r\n" +
            "Bonus Rewards\r\n" +
            "The following rewards will be awarded to you as a bonus if you complete the mission within 6 hours:\r\n" +
            " \t1.610.000 ISK");
        Run run = await WaitForRunningMissionAsync(env);
        await using ClientDbContext db = await env.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync();
        var parameters = await db.Set<RunParameter>().Where(p => p.RunId == run.Id).ToListAsync();

        Assert.Equal(4, parameters.Count);
        Assert.Contains(parameters, p => p.ParameterKey == RunParameterKey.Isk && p.Amount == 1_000_000m);
        Assert.Contains(parameters, p => p.ParameterKey == RunParameterKey.BonusIsk && p.Amount == 1_610_000m
            && p.BonusWindowSeconds == 21600);
        Assert.Contains(parameters, p => p.ParameterKey == RunParameterKey.LoyaltyPoints && p.Amount == 5_000m);
        Assert.Contains(parameters, p => p.ParameterKey == RunParameterKey.Item && p.Amount == 3m && p.ItemTypeId == 34);
    }

    [AvaloniaFact]
    public async Task AMissionCapture_WithAnUnrecognisedReward_KeepsItAlongsideKnownRewards()
    {
        using var env = await Env.StartAsync();
        env.Sde.AddAgent(AralinJick);
        await env.AddCharacterAsync();

        env.Copy(_MissionCapture(" \t1.000.000 ISK\t", " \tMysterious reward\t"));
        Run run = await WaitForRunningMissionAsync(env);
        await using ClientDbContext db = await env.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync();
        List<RunParameter> parameters = await db.Set<RunParameter>().Where(parameter => parameter.RunId == run.Id).ToListAsync();

        Assert.Contains(parameters, parameter => parameter.ParameterKey == RunParameterKey.Isk && parameter.Amount == 1_000_000m);
        Assert.Contains(parameters, parameter => parameter.ParameterKey == RunParameterKey.Unknown);
    }

    [AvaloniaFact]
    public async Task AMissionCapture_WithOnlyKnownRewards_DoesNotCreateAnUnknownParameter()
    {
        using var env = await Env.StartAsync();
        env.Sde.AddAgent(AralinJick);
        await env.AddCharacterAsync();

        env.Copy(_MissionCapture(" \t1.000.000 ISK\t", " \t1.610.000 ISK\t"));
        Run run = await WaitForRunningMissionAsync(env);
        await using ClientDbContext db = await env.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync();
        List<RunParameter> parameters = await db.Set<RunParameter>().Where(parameter => parameter.RunId == run.Id).ToListAsync();

        Assert.DoesNotContain(parameters, parameter => parameter.ParameterKey == RunParameterKey.Unknown);
    }

    [AvaloniaFact]
    public async Task AMissionCapture_WithAnUnrecognisedReward_PreservesItsRawClipboardLine()
    {
        using var env = await Env.StartAsync();
        env.Sde.AddAgent(AralinJick);
        await env.AddCharacterAsync();

        const string rawLine = " \tMysterious reward\t ";
        env.Copy(_MissionCapture(rawLine, " \t1.000.000 ISK\t"));
        Run run = await WaitForRunningMissionAsync(env);
        await using ClientDbContext db = await env.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync();
        RunParameter parameter = await db.Set<RunParameter>().SingleAsync(parameter =>
            parameter.RunId == run.Id && parameter.ParameterKey == RunParameterKey.Unknown);

        Assert.Equal(RunParameterKey.Unknown, parameter.ParameterKey);
        Assert.Equal(rawLine + "\r", parameter.TypedValue);
    }

    [AvaloniaTheory]
    [MemberData(nameof(MeasuredMissionWhitespaceVariants))]
    public async Task AMissionCapture_WithWhitespaceVariants_WritesMeasuredRewards(string text, decimal expectedIsk)
    {
        using var env = await Env.StartAsync();
        env.Sde.AddAgent(AralinJick);
        await env.AddCharacterAsync();

        env.Copy(text);
        Run run = await WaitForRunningMissionAsync(env);
        await using ClientDbContext db = await env.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync();
        List<RunParameter> parameters = await db.Set<RunParameter>().Where(parameter => parameter.RunId == run.Id).ToListAsync();

        Assert.Collection(parameters.OrderBy(parameter => parameter.ParameterKey),
            parameter =>
            {
                Assert.Equal(RunParameterKey.Isk, parameter.ParameterKey);
                Assert.Equal(expectedIsk, parameter.Amount);
            },
            parameter =>
            {
                Assert.Equal(RunParameterKey.BonusIsk, parameter.ParameterKey);
                Assert.Equal(1_610_000m, parameter.Amount);
                Assert.Equal(21600, parameter.BonusWindowSeconds);
            });
    }

    // AC-8 countercheck: a made-up agent name — the SDE import is a snapshot, and CCP adds agents. A miss must not
    // block the run.
    [AvaloniaFact]
    public async Task AMissionForAnAgentTheSdeDoesNotKnow_StillStartsARun_WithoutALevel_AndWithANotice()
    {
        using var env = await Env.StartAsync(); // Sde deliberately carries no agents at all
        await env.AddCharacterAsync();

        env.Copy(MeasuredMissionBlock.Replace("Aralin Jick", "Zorvanna Ixthil"));
        Run run = await WaitForRunningMissionAsync(env);
        Assert.Equal(ActivityKind.Mission, run.ActivityKind);
        Assert.Null(run.AgentId);
        Assert.Null(run.MissionLevel);
        Assert.Contains(env.Toasts.Toasts, t => t.Title == "Agent not recognised");
    }

    /// <summary>Polls without blocking the UI-thread synchronization context the headless tests run on — an
    /// <c>ActivityWindowHarness.WaitUntil</c>-style synchronous condition would have to block on this same async
    /// database read, which deadlocks under that context instead of yielding to it.</summary>
    private static async Task<Run> WaitForRunningMissionAsync(Env env, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            if (await env.RunningMissionAsync() is { } run)
                return run;

            await Task.Delay(25);
        }

        throw new Xunit.Sdk.XunitException("no mission run started within the timeout");
    }

    private static string _Fixture(string name)
    {
        DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EVE-Together.slnx")))
            directory = directory.Parent;

        return File.ReadAllText(Path.Combine(
            directory?.FullName ?? throw new InvalidOperationException("the solution root is not above the test binary"),
            "EveUtils.Client.UiTests", "Fixtures", name));
    }

    private static string _MissionCapture(params string[] rewards) =>
        "Aralin Jick Objectives\r\n" +
        "The following objectives must be completed to finish the mission:\r\n" +
        "\r\n" +
        "Report to Aralin Jick\r\n" +
        " \tAgent Location\t0,6 Nishah VII - Moon 5 - Kor-Azor Family Treasury\r\n" +
        "\r\n" +
        "Rewards\r\n" +
        "The following rewards will be yours if you complete this mission:\r\n" +
        string.Join("\r\n", rewards);

    private sealed class Env : IDisposable
    {
        private readonly TestClientInstance _instance;
        private readonly ClipboardWatchService _watch;
        private readonly ClipboardMissionOffer _offer;
        private readonly FakeClipboardChangeSource _source;
        private readonly Window _owner;

        public RecordingToastService Toasts { get; } = new();

        public FakeSdeAccessor Sde { get; } = new();

        public DialogService Dialogs { get; }

        public IServiceProvider Services => _instance.Services;

        private Env(TestClientInstance instance, ClipboardWatchService watch, FakeClipboardChangeSource source, Window owner)
        {
            _instance = instance;
            _watch = watch;
            _source = source;
            _owner = owner;
            Dialogs = new DialogService();
            Dialogs.SetOwner(owner);
            _offer = new ClipboardMissionOffer(watch, Toasts, Sde, Dialogs, instance.Services);
        }

        public static async Task<Env> StartAsync()
        {
            var source = new FakeClipboardChangeSource();
            var instance = TestClientInstance.Create();
            var watch = new ClipboardWatchService(new RecordingDialogService(), instance.Services,
                NullLogger<ClipboardWatchService>.Instance, source);
            var owner = new Window { Width = 200, Height = 200 };
            owner.Show();
            var env = new Env(instance, watch, source, owner);
            await watch.SetEnabledAsync(true);
            return env;
        }

        public async Task AddCharacterAsync() =>
            await Services.GetRequiredService<ICharacterRegistry>().AddOrUpdateAsync(new Character("Test Pilot", 90000001));

        public void Copy(string text)
        {
            _source.ClipboardText = text;
            _source.RaiseChanged();
            Dispatcher.UIThread.RunJobs();
        }

        public async Task<Run?> RunningMissionAsync()
        {
            await using ClientDbContext db = await Services.GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync();
            return await db.Set<Run>().AsNoTracking()
                .SingleOrDefaultAsync(run => run.ActivityKind == ActivityKind.Mission && run.State == RunState.Running);
        }

        public void Dispose()
        {
            Dialogs.CloseAllPopouts();
            _owner.Close();
            _offer.Dispose();
            _watch.Dispose();
            // A window opened before the copy (the "already open" AC-1 row) still has its own LoadAsync in flight —
            // draining it here, instead of only where the test happens to wait, is what keeps that continuation from
            // reaching a disposed provider after the block below tears it down.
            for (var drain = 0; drain < 10; drain++)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(10);
            }

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
}
