using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Formatting;
using EveUtils.Client.Gamelog;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Client.ViewModels.Runs.Sections;
using EveUtils.Shared.Data;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Gamelog.Models;
using EveUtils.Shared.Modules.Market.Entities;
using EveUtils.Shared.Modules.Market.Repositories;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Isk;
using EveUtils.Shared.Modules.Runs.Queries;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using IDispatcher = EveUtils.Shared.Cqrs.IDispatcher;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-256: a run's TOTAL ISK is the sum of what each source contributes, added up once by
/// <see cref="IskContributors"/> and shown as that one figure by every screen. Measured before this ticket: the runs
/// overview read a mission at +20.54M (bounty and loot) while its detail screen read 25,461,587 (with the mission's
/// ISK and bonus) — four screens, four formulas.
/// </summary>
public sealed class RunIskTotalTests
{
    private const int LootTypeId = 34;
    private const int SecondCharacterId = 90000002;

    // ── The same total on every screen ──────────────────────────────────────────────────────────────

    /// <summary>
    /// One run per sample, flown through the real run window and then read back by every screen that shows its total:
    /// the window itself, UNFINISHED once it is stopped, and — once it is saved — the runs overview row, its day band,
    /// the detail screen and "ISK today". The expected figure is worked out by hand from what the run was given.
    ///
    /// Counter-proof, run against <c>origin/main</c> a8dfe86 without this ticket — every sample red:
    /// <list type="bullet">
    /// <item>mission with a bonus: the overview row and the day band read +20.54M where the detail screen read
    /// 25,461,587 (Jithran's own measurement, reproduced); "ISK today" read 14,100,000, bounty alone;</item>
    /// <item>mission with an expired bonus: UNFINISHED still counted the bonus (25,461,587), the overview read
    /// +20.54M, the detail screen 23,001,587 — three screens, three figures;</item>
    /// <item>abyssal with loot: UNFINISHED read "0 ISK" for a run at -200, "ISK today" 0;</item>
    /// <item>combat site and group run: "ISK today" left the loot out.</item>
    /// </list>
    /// </summary>
    [AvaloniaTheory]
    [InlineData("combat site")]
    [InlineData("mission with a bonus")]
    [InlineData("mission with an expired bonus")]
    [InlineData("abyssal with loot")]
    [InlineData("group run")]
    public async Task SampleRun_ShowsTheSameTotal_OnEveryScreen(string name)
    {
        Sample sample = Samples.Single(candidate => candidate.Name == name);
        using ActivityWindowHarness harness = await _HarnessAsync(sample);
        IDispatcher dispatcher = harness.Services.GetRequiredService<IDispatcher>();
        await harness.Services.GetRequiredService<IMarketPriceRepository>().ReplaceAllAsync(
        [
            new LocalMarketPrice
            {
                TypeId = LootTypeId, AveragePrice = sample.LootPrice, AdjustedPrice = sample.LootPrice,
                UpdatedAt = DateTimeOffset.UtcNow
            }
        ]);

        ActivityWindowViewModel window = await _FlyAsync(harness, sample);
        window.StopRun(DateTime.UtcNow);
        await _SettleAsync(window, sample.Total);
        Assert.Equal(IskFormat.Whole(sample.Total), window.GroupTotalIskText);

        IReadOnlyList<UnfinishedRunDto> unfinished = await _UnfinishedAsync(dispatcher, sample.Group ? 2 : 1);
        Assert.Equal(sample.Total, unfinished.Sum(run => run.TotalIsk));
        if (!sample.Group)
            Assert.Equal(IskFormat.Whole(sample.Total), new UnfinishedRunViewModel(unfinished[0],
                ActivityWindowHarness.CharacterName, _ => Task.CompletedTask, _ => Task.CompletedTask,
                _ => Task.CompletedTask).TotalIskText);

        await window.SaveRunCommand.ExecuteAsync(null);

        var overview = new RunsOverviewViewModel(dispatcher, harness.Dialogs, harness.Services, _Crew(sample), runClock: false);
        await overview.LoadAsync(TestContext.Current.CancellationToken);
        RunsDayViewModel day = Assert.Single(overview.Tabs[0].Days);
        ActivityOverviewRowViewModel row = Assert.Single(day.Rows);
        string signed = (sample.Total < 0 ? string.Empty : "+") + IskFormat.Compact(sample.Total) + " ISK";
        Assert.Equal(sample.Total, row.NetIsk);
        Assert.Equal(signed, row.NetText);
        Assert.EndsWith($"{signed} net", day.SummaryText);

        var detail = new ActivityDetailViewModel(dispatcher, row.ActivitySummaryId);
        await detail.LoadAsync(TestContext.Current.CancellationToken);
        Assert.True(detail.HasTotalIsk);
        Assert.Equal(IskFormat.Whole(sample.Total), detail.TotalIskText);

        Result<decimal> today = await dispatcher.Query(new GetIskTodayQuery(DateTime.UtcNow.AddHours(-1),
            [.. _Crew(sample).Select(character => (long)character.EsiCharacterId!.Value)]));
        Assert.Equal(sample.Total, today.Value);
    }

    /// <summary>A summary saved before this ticket has no total and no breakdown, and the runs overview would read it
    /// as "no loot or bounty recorded". The startup check adds it up again — once: a store already built by today's
    /// sources is left alone. Counter-proof: skip the check and the old row keeps its empty total.</summary>
    [Fact]
    public async Task SummarySavedBeforeTotalsWereStored_IsAddedUpAgainAtStartup_OnlyOnce()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        DateTime startedAtUtc = DateTime.UtcNow.AddHours(-1);
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(ActivityWindowHarness.CharacterId,
            ActivityKind.Site, startedAtUtc, 1234, "Homefront", 30000142), cancellationToken);
        await dispatcher.Send(new SaveRunCommand(started.Value, startedAtUtc.AddMinutes(15), startedAtUtc.AddMinutes(16), [],
            [new RunBountyEntryInput { OccurredAtUtc = startedAtUtc.AddMinutes(5), Isk = 750_000m }], [], []), cancellationToken);
        await using (ClientDbContext db = await _DbAsync(instance, cancellationToken))
            await db.Set<ActivitySummary>().ExecuteUpdateAsync(setters => setters
                .SetProperty(summary => summary.TotalIsk, (decimal?)null)
                .SetProperty(summary => summary.IskContributions, (string?)null)
                .SetProperty(summary => summary.IskSources, (string?)null), cancellationToken);

        Result<int> first = await dispatcher.Send(new RebuildActivitySummariesCommand(OnlyWhenOutdated: true), cancellationToken);
        Result<int> second = await dispatcher.Send(new RebuildActivitySummariesCommand(OnlyWhenOutdated: true), cancellationToken);

        Assert.Equal(1, first.Value);
        Assert.Equal(0, second.Value);
        await using ClientDbContext check = await _DbAsync(instance, cancellationToken);
        ActivitySummary summary = await check.Set<ActivitySummary>().SingleAsync(cancellationToken);
        Assert.Equal(750_000m, summary.TotalIsk);
        Assert.Equal(IskContributors.Signature, summary.IskSources);
    }

    // ── The registry ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Signed: loot that lost more than it gained takes from the total, it is not floored at zero.</summary>
    [Fact]
    public void Total_IsTheSignedSumOfEveryContribution()
    {
        IskBreakdown isk = IskContributors.Breakdown([_Facts(bounty: 1_000m, lootNet: -300m)], DateTime.UtcNow);

        Assert.Equal(700m, isk.Total);
        Assert.Equal(1_000m, isk.Of(IskSource.Bounty)?.Amount);
        Assert.Equal(-300m, isk.Of(IskSource.Loot)?.Amount);
        Assert.Null(isk.Of(IskSource.Rewards));
    }

    /// <summary>Every own toon's run started from one mission window carries its own copy of the mission's reward
    /// lines (ET-210), and the mission pays once. Counter-proof: sum the lines over every run, as the detail screen
    /// did before this ticket, and this reads 4,920,000.</summary>
    [Fact]
    public void RewardsOfOneMission_CarriedByEveryOwnToonsRun_CountOnce()
    {
        DateTime copiedAtUtc = DateTime.UtcNow.AddMinutes(-5);
        RunIskParameter isk = new(RunParameterKey.Isk, 2_460_000m, null, copiedAtUtc);

        IskBreakdown sameMission = IskContributors.Breakdown(
            [_Facts(parameters: [isk]), _Facts(parameters: [isk])], DateTime.UtcNow);
        IskBreakdown twoMissions = IskContributors.Breakdown(
            [_Facts(parameters: [isk]), _Facts(parameters: [isk with { ObservedAtUtc = copiedAtUtc.AddMinutes(1) }])],
            DateTime.UtcNow);

        Assert.Equal(2_460_000m, sameMission.Total);
        Assert.Equal(4_920_000m, twoMissions.Total);
    }

    /// <summary>The bonus is judged at the run's own STOP, and at "now" only while it is still going — one rule for
    /// the total and for both MISSION sections (<see cref="MissionBonusDeadline"/>).</summary>
    [Fact]
    public void Bonus_CountsWhenTheRunStoppedInTime_AndNotAfter()
    {
        DateTime copiedAtUtc = new(2026, 9, 12, 12, 0, 0, DateTimeKind.Utc);
        RunIskParameter bonus = new(RunParameterKey.BonusIsk, 2_460_000m, 3600, copiedAtUtc);
        DateTime longAfter = copiedAtUtc.AddHours(5);

        Assert.Equal(2_460_000m, IskContributors.Breakdown(
            [_Facts(parameters: [bonus], stoppedAtUtc: copiedAtUtc.AddMinutes(59))], longAfter).Total);
        Assert.False(IskContributors.Breakdown(
            [_Facts(parameters: [bonus], stoppedAtUtc: copiedAtUtc.AddMinutes(60))], longAfter).HasFigure);
        Assert.False(IskContributors.Breakdown([_Facts(parameters: [bonus])], longAfter).HasFigure);
    }

    /// <summary>A mission's own stated Bounty line is not money that arrived — the gamelog's bounty already counts it
    /// — and LP has no ISK rate.</summary>
    [Fact]
    public void StatedBountyLineAndLoyaltyPoints_CountNothing()
    {
        DateTime nowUtc = DateTime.UtcNow;
        IskBreakdown isk = IskContributors.Breakdown([_Facts(parameters:
        [
            new RunIskParameter(RunParameterKey.Bounty, 900_000m, null, nowUtc),
            new RunIskParameter(RunParameterKey.LoyaltyPoints, 1_150m, null, nowUtc)
        ])], nowUtc);

        Assert.Empty(isk.Contributions);
    }

    /// <summary>Loot nobody can price is no figure at all, never a zero (ET-217) — and a bounty beside it makes the
    /// total a real one again.</summary>
    [Fact]
    public void LootNobodyCanPrice_IsNoFigure_UntilSomethingElseIs()
    {
        IskBreakdown unpriced = IskContributors.Breakdown([_Facts(hasLoot: true)], DateTime.UtcNow);
        IskBreakdown withBounty = IskContributors.Breakdown([_Facts(bounty: 500m, hasLoot: true)], DateTime.UtcNow);

        Assert.False(unpriced.HasFigure);
        Assert.True(unpriced.IsUnvalued);
        Assert.Equal(IskCertainty.Unknown, unpriced.Of(IskSource.Loot)?.Certainty);
        Assert.True(withBounty.HasFigure);
        Assert.Equal(500m, withBounty.Total);
    }

    /// <summary>A future source that is owed rather than paid — a homefront payout (ET-231) — reaches the screens
    /// without them being touched: the row counts it and says part of the figure is expected.</summary>
    [Fact]
    public void AnExpectedContribution_CountsTowardsTheTotal_AndTheRowSaysSo()
    {
        IskBreakdown isk = new(
        [
            new IskContribution(IskSource.Bounty, 1_000_000m, IskCertainty.Measured),
            new IskContribution((IskSource)99, 500_000m, IskCertainty.Expected)
        ]);
        var row = new ActivityOverviewRowViewModel(
            new ActivityOverviewRowDto(Guid.NewGuid(), null, Guid.NewGuid(), ActivityKind.Site, "Homefront", null, null,
                DateTime.UtcNow, 600, 1, 1, [], [], 1_000_000m, null, 0, false, false, [], isk),
            id => $"character {id}", _ => Task.CompletedTask, _ => Task.CompletedTask);

        Assert.Equal(1_500_000m, row.NetIsk);
        Assert.Equal("+1.5M ISK (part expected)", row.NetText);
    }

    // ── Every source has a section, every section its source ────────────────────────────────────────

    /// <summary>A source nobody shows is ISK the total counts and no screen explains. Counter-proof: take
    /// <c>IskSource.Bounty</c> off the BOUNTY module and this goes red on Bounty.</summary>
    [Fact]
    public void EveryContributor_IsShownByExactlyOneSection()
    {
        foreach (IIskContributor contributor in IskContributors.All)
            Assert.True(RunSectionModules.All.Count(module => module.IskSource == contributor.Source) == 1,
                $"{contributor.Source} is counted in TOTAL ISK but no single section module claims it");

        Assert.Equal(IskContributors.All.Count, IskContributors.All.Select(contributor => contributor.Source).Distinct().Count());
    }

    /// <summary>A section that claims a source nobody adds up shows ISK that is missing from the total — what the runs
    /// overview did with a mission's rewards. Counter-proof: take the rewards contributor out of the registry and this
    /// goes red on MISSION.</summary>
    [Fact]
    public void EverySectionClaimingASource_HasItsContributorRegistered()
    {
        foreach (RunSectionModule module in RunSectionModules.All.Where(module => module.IskSource is not null))
            Assert.True(IskContributors.All.Any(contributor => contributor.Source == module.IskSource),
                $"{module.Id} shows {module.IskSource}, which no contributor adds to TOTAL ISK");
    }

    /// <summary>A section that puts an ISK figure on screen without claiming a source is how a new module slips past
    /// the total. Counter-proof: take <c>IskSource.Bounty</c> off the BOUNTY module and this goes red on both of its
    /// sections.</summary>
    [AvaloniaFact]
    public void EverySectionShowingIsk_ClaimsTheSourceItShows()
    {
        using var instance = TestClientInstance.Create();
        var window = new ActivityWindowViewModel(ActivityKind.Site, instance.Services);
        var services = new RunDetailSectionServices(instance.Services.GetRequiredService<IDispatcher>(),
            null, null, null, null, null, null, null, null);
        List<string> offences = [];

        foreach (RunSectionModule module in RunSectionModules.All.Where(module => module.IskSource is null))
        {
            List<Type> sections = [];
            if (module.CreateForWindow is { } createForWindow)
                sections.Add(createForWindow(window).GetType());
            if (module.CreateForDetail is { } createForDetail)
                sections.Add(createForDetail(services).GetType());

            foreach (Type section in sections.Where(section => _ShowsIsk(section)
                         && !ShowsOthersIsk.Any(kept => kept.Section == section.Name)))
                offences.Add($"{section.Name} shows ISK, but its module claims no IskSource");
        }

        Assert.True(offences.Count == 0, string.Join("\n", offences));
    }

    /// <summary>
    /// The shape this ticket removed: a screen adding a bounty to a loot figure, or a reward to either, on its own.
    /// Every such sum is <see cref="IskContributors"/>'s, and nothing outside <c>Modules/Runs/Isk</c> writes one.
    ///
    /// Counter-proof: the same rule over <c>origin/main</c> a8dfe86 finds exactly the four formulas this ticket started
    /// from — the runs overview row, the detail screen, the run window and <c>TotalIskCalculator</c>.
    /// </summary>
    [Fact]
    public void NoScreen_AddsUpATotalOfItsOwn()
    {
        string isk = Path.Combine(_SourcePath("EveUtils.Shared"), "Modules", "Runs", "Isk");
        List<string> offences = [];
        foreach (string file in new[] { "EveUtils.Client", "EveUtils.Shared" }
                     .SelectMany(project => Directory.EnumerateFiles(_SourcePath(project), "*.cs", SearchOption.AllDirectories))
                     .Where(file => !file.StartsWith(isk, StringComparison.OrdinalIgnoreCase)
                                    && !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                                    && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
        {
            string[] lines = File.ReadAllLines(file);
            for (int index = 0; index < lines.Length; index++)
                if (IskFiguresAdded.IsMatch(lines[index]))
                    offences.Add($"{Path.GetFileName(file)}:{index + 1}: {lines[index].Trim()}");
        }

        Assert.True(offences.Count == 0,
            "A total of its own outside the ISK registry — register a contributor instead:\n" + string.Join("\n", offences));
    }

    // Two of bounty, loot and reward ISK figures on one line with a plus between them.
    private static readonly Regex IskFiguresAdded = new(
        @"\b(?:bounty|loot|reward)\w*isk\w*\b[^;]*?\s\+\s[^;]*?\b(?:bounty|loot|reward)\w*isk\w*\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Sections that show ISK which is not this run's own, each with its reason.</summary>
    private static readonly (string Section, string Reason)[] ShowsOthersIsk =
    [
        (nameof(FleetWindowSectionViewModel),
            "what fleetmates share about their own runs over the fleet stream — their figures, not a share of this run's total")
    ];

    // What puts an ISK figure on screen: the ISK formatter, a view model's ISK readout, or a view binding one.
    private static readonly Regex IskInViewModel = new(@"IskFormat\.|\bIsk\(|IskDisplay\b|IskText\b", RegexOptions.Compiled);
    private static readonly Regex IskInView = new(@"\{Binding [^}]*Isk", RegexOptions.Compiled);

    private static bool _ShowsIsk(Type section)
    {
        string viewModel = Path.Combine(_SourcePath("EveUtils.Client"), "ViewModels", "Runs", "Sections", $"{section.Name}.cs");
        string view = Path.Combine(_SourcePath("EveUtils.Client"), "Views", "Runs", "Sections",
            $"{section.Name.Replace("ViewModel", "View", StringComparison.Ordinal)}.axaml");
        return IskInViewModel.IsMatch(File.ReadAllText(viewModel))
               || (File.Exists(view) && IskInView.IsMatch(File.ReadAllText(view)));
    }

    // ── The samples ─────────────────────────────────────────────────────────────────────────────────

    /// <param name="Bounty">What the starter's gamelog pays out while the run goes.</param>
    /// <param name="Loot">Per character, how many of the one priced item were gained and lost.</param>
    /// <param name="Parameters">The mission's reward lines, relative to the moment the window opens.</param>
    private sealed record Sample(
        string Name, ActivityKind Kind, bool Group, double LootPrice, long Bounty,
        IReadOnlyList<(int CharacterId, long Gained, long Lost)> Loot,
        Func<DateTime, IReadOnlyList<RunParameterInput>> Parameters,
        decimal Total);

    private static readonly Sample[] Samples =
    [
        new("combat site", ActivityKind.Site, Group: false, LootPrice: 100, Bounty: 1_500_000,
            [(ActivityWindowHarness.CharacterId, 3, 0)], _ => [], Total: 1_500_300m),
        // Jithran's own "Mining Misappropriation": bounty 14,100,000, loot 6,441,587, ISK 2,460,000, bonus 2,460,000.
        new("mission with a bonus", ActivityKind.Mission, Group: false, LootPrice: 6_441_587, Bounty: 14_100_000,
            [(ActivityWindowHarness.CharacterId, 1, 0)], openedAtUtc => _MissionRewards(openedAtUtc.AddMinutes(-10)),
            Total: 25_461_587m),
        new("mission with an expired bonus", ActivityKind.Mission, Group: false, LootPrice: 6_441_587, Bounty: 14_100_000,
            [(ActivityWindowHarness.CharacterId, 1, 0)], openedAtUtc => _MissionRewards(openedAtUtc.AddHours(-2)),
            Total: 23_001_587m),
        // An abyssal that burned more than it brought home: filaments and ammunition up, one item back.
        new("abyssal with loot", ActivityKind.Abyssal, Group: false, LootPrice: 100, Bounty: 0,
            [(ActivityWindowHarness.CharacterId, 1, 3)], _ => [], Total: -200m),
        new("group run", ActivityKind.Site, Group: true, LootPrice: 100, Bounty: 1_000_000,
            [(ActivityWindowHarness.CharacterId, 3, 0), (SecondCharacterId, 2, 0)], _ => [], Total: 1_000_500m)
    ];

    // "within 1 hour" at the moment of the copy.
    private static IReadOnlyList<RunParameterInput> _MissionRewards(DateTime copiedAtUtc) =>
    [
        new RunParameterInput { ParameterKey = RunParameterKey.Isk, TypedValue = "2460000", Amount = 2_460_000m, ObservedAtUtc = copiedAtUtc },
        new RunParameterInput
        {
            ParameterKey = RunParameterKey.BonusIsk, TypedValue = "2460000", Amount = 2_460_000m,
            BonusWindowSeconds = 3600, ObservedAtUtc = copiedAtUtc
        }
    ];

    private static IReadOnlyList<Character> _Crew(Sample sample) => sample.Group
        ? [new Character(ActivityWindowHarness.CharacterName, ActivityWindowHarness.CharacterId), new Character("Second Pilot", SecondCharacterId)]
        : [new Character(ActivityWindowHarness.CharacterName, ActivityWindowHarness.CharacterId)];

    private static async Task<ActivityWindowHarness> _HarnessAsync(Sample sample)
    {
        if (!sample.Group)
            return await ActivityWindowHarness.CreateAsync();

        ActivityWindowHarness harness = await ActivityWindowHarness.CreateAsync(configure: services =>
            services.AddSingleton<EveUtils.Client.Platform.ILocalCharacterPresence>(
                new ActivityWindowHarness.StubPresence(inGame: true, ActivityWindowHarness.CharacterId, SecondCharacterId)));
        await harness.Services.GetRequiredService<ICharacterRegistry>().AddOrUpdateAsync(new Character("Second Pilot", SecondCharacterId));
        return harness;
    }

    /// <summary>START, the bounty coming in through the gamelog and the loot through a capture per character — the way
    /// the application feeds a run — until the window's own total has caught up with all of it.</summary>
    private static async Task<ActivityWindowViewModel> _FlyAsync(ActivityWindowHarness harness, Sample sample)
    {
        var window = new ActivityWindowViewModel(sample.Kind, harness.Services) { PendingParameters = sample.Parameters(DateTime.UtcNow) };
        await window.LoadAsync();
        if (sample.Kind is ActivityKind.Abyssal)
        {
            await window.Activity().SelectTierCommand.ExecuteAsync(2);
            await window.Activity().SelectWeatherCommand.ExecuteAsync(0);
        }
        if (sample.Group)
            harness.Dialogs.OnPickCharacters = (_, options) =>
                Task.FromResult<IReadOnlyList<int>?>([.. options.Select(option => option.CharacterId)]);
        await window.StartRunCommand.ExecuteAsync(null);
        await ActivityWindowHarness.WaitUntil(() => window.RunId is not null && (!sample.Group || window.Participants.Count == 2));
        Guid ownRunId = window.RunId ?? throw new InvalidOperationException("START gave the window no run");

        if (sample.Bounty > 0)
        {
            var gamelog = harness.Services.GetRequiredService<GamelogClientService>();
            gamelog.MapCharacter(ActivityWindowHarness.CharacterId, ActivityWindowHarness.CharacterName);
            await gamelog.AddBountyAsync(ActivityWindowHarness.CharacterName, new BountyEvent(DateTime.UtcNow, sample.Bounty));
        }

        IDispatcher dispatcher = harness.Services.GetRequiredService<IDispatcher>();
        foreach ((int characterId, long gained, long lost) in sample.Loot)
        {
            Guid runId = characterId == ActivityWindowHarness.CharacterId
                ? ownRunId
                : window.Participants.Single(participant => participant.CharacterId == characterId).RunId;
            List<RunLootEntryInput> entries = [];
            if (gained > 0)
                entries.Add(new RunLootEntryInput { ItemTypeId = LootTypeId, Name = "Tritanium", Quantity = gained, LootKind = LootKind.Gained });
            if (lost > 0)
                entries.Add(new RunLootEntryInput { ItemTypeId = LootTypeId, Name = "Tritanium", Quantity = lost, LootKind = LootKind.Lost });
            await dispatcher.Send(new AddRunLootCaptureCommand(new RunLootCaptureInput
            {
                CapturedAtUtc = DateTime.UtcNow, Source = LootCaptureSource.Clipboard, PreferredRunId = runId, Entries = entries
            }));
        }

        await _SettleAsync(window, sample.Total);
        return window;
    }

    // The loot cache fills in off RunLootCapturedEvent, so each poll runs the clock's own tick rather than waiting for it.
    private static Task _SettleAsync(ActivityWindowViewModel window, decimal total) =>
        ActivityWindowHarness.WaitUntil(() =>
        {
            window.Refresh(DateTime.UtcNow);
            return window.GroupTotalIskText == IskFormat.Whole(total);
        });

    private static async Task<IReadOnlyList<UnfinishedRunDto>> _UnfinishedAsync(IDispatcher dispatcher, int expected)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        IReadOnlyList<UnfinishedRunDto> runs = [];
        while (DateTime.UtcNow < deadline)
        {
            runs = (await dispatcher.Query(new GetUnfinishedRunsQuery())).Value ?? [];
            if (runs.Count == expected)
                break;

            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            await Task.Delay(25);
        }

        Assert.Equal(expected, runs.Count);
        return runs;
    }

    private static Task<ClientDbContext> _DbAsync(TestClientInstance instance, CancellationToken cancellationToken) =>
        instance.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(cancellationToken);

    private static RunIskFacts _Facts(decimal bounty = 0m, decimal? lootNet = null, bool hasLoot = false,
        IReadOnlyList<RunIskParameter>? parameters = null, DateTime? stoppedAtUtc = null) => new()
    {
        BountyIsk = bounty,
        LootIskNet = lootNet,
        HasLoot = hasLoot || lootNet is not null,
        Parameters = parameters ?? [],
        StoppedAtUtc = stoppedAtUtc
    };

    /// <summary>The repository folder, found from the test binary rather than from a checkout path baked in here.</summary>
    private static string _SourcePath(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EVE-Together.slnx")))
            directory = directory.Parent;

        return Path.Combine(
            directory?.FullName ?? throw new InvalidOperationException("the solution root is not above the test binary"),
            relative);
    }
}
