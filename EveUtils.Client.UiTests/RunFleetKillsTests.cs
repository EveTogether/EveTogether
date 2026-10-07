using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using EveUtils.Client.Fleet;
using EveUtils.Client.Formatting;
using EveUtils.Client.ViewModels.Killmails;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Client.ViewModels.Runs.Sections;
using EveUtils.Client.Views;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Killmails.Entities;
using EveUtils.Shared.Modules.Killmails.Repositories;
using EveUtils.Shared.Modules.Market.Entities;
using EveUtils.Shared.Modules.Market.Repositories;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Queries;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Sde.Dtos;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using UiDispatcher = Avalonia.Threading.Dispatcher;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-373: the run info of a group run names the pilot in LINKED LOSS and gets a FLEET KILLS section — one line per
/// killmail, inside the group's period only — whose destroyed sum is information and never part of TOTAL ISK.
/// </summary>
public sealed class RunFleetKillsTests
{
    private const int Own = 95373001;
    private const int MateOne = 95373002;
    private const int MateTwo = 95373003;
    private const int Enemy = 95379999;
    private const int Jita = 30000142;
    private const int Gila = 17715;
    private const int Rifter = 587;
    private const int Capsule = 670;
    private const int Repairer = 3001;
    private const int Salvage = 3002;
    private const int Unpriced = 3003;
    private const string Group = "G373";
    private static readonly DateTime StartedAtUtc = new(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime StoppedAtUtc = StartedAtUtc.AddMinutes(20);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string? ShotsDirectory => Environment.GetEnvironmentVariable("ET373_SHOTS");

    [AvaloniaFact]
    public async Task GroupRun_ShowsFleetKillsOncePerMailInsideThePeriod_AndNamesThePilotOfEveryLoss()
    {
        using TestClientInstance instance = _Instance();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        ILocalKillmailRepository store = instance.Services.GetRequiredService<ILocalKillmailRepository>();
        Guid ownRun = await _SaveRunAsync(dispatcher, Own, "Ravnholt", Group);
        Guid mateRun = await _SaveRunAsync(dispatcher, MateOne, "Tessa Korrin", Group);
        await _SaveRunAsync(dispatcher, MateTwo, "Doro Vanth", Group);
        Guid soloRun = await _SaveRunAsync(dispatcher, Own, "Ravnholt", null, StartedAtUtc.AddHours(3));

        // Kill 1: three fleet members on one mail, so three copies. Kill 2: a Gila and, 20 s later, its pilot's pod, one
        // minute after the group stopped (still in). Kill 3: three minutes after (out). Kill 9: only the solo run's.
        foreach (int pilot in new[] { Own, MateOne, MateTwo })
        {
            await store.AddMissingAsync(pilot, [_Kill(pilot, 1, Rifter, StartedAtUtc.AddMinutes(10), Enemy)]);
        }

        await store.AddMissingAsync(Own, [
            _Kill(Own, 2, Gila, StoppedAtUtc.AddMinutes(1), Enemy + 1),
            _Kill(Own, 5, Capsule, StoppedAtUtc.AddMinutes(1).AddSeconds(20), Enemy + 1),
            _Kill(Own, 3, Rifter, StoppedAtUtc.AddMinutes(3), Enemy + 2),
            _Kill(Own, 9, Rifter, StartedAtUtc.AddHours(3).AddMinutes(5), Enemy + 3),
            // The same mail as the mate's loss, seen from the pilot who attacked on it: a loss, never a kill.
            _Kill(Own, 21, Gila, StartedAtUtc.AddMinutes(9), MateOne),
            // Friendly fire: a member killed a member whose own loss row is not stored; still no kill.
            _Kill(Own, 22, Rifter, StartedAtUtc.AddMinutes(11), MateTwo),
            _Loss(Own, 20, Gila, StartedAtUtc.AddMinutes(8), ownRun)
        ]);
        await store.AddMissingAsync(MateOne, [_Loss(MateOne, 21, Gila, StartedAtUtc.AddMinutes(9), mateRun)]);
        await instance.Services.GetRequiredService<IMarketPriceRepository>().ReplaceAllAsync(
            [_Price(Gila, 95_000_000), _Price(Rifter, 1_800_000), _Price(Capsule, 0)], Ct);
        await dispatcher.Send(new RebuildActivitySummariesCommand(), Ct);

        ActivityDetailViewModel group = await _LoadAsync(instance, dispatcher, ownRun);
        FleetKillsDetailSectionViewModel fleetKills = group.Sections.OfType<FleetKillsDetailSectionViewModel>().Single();
        Assert.Equal(2, fleetKills.Kills.Count);
        FleetKillViewModel shared = fleetKills.Kills[0];
        Assert.Equal("Rifter", shared.ShipText);
        Assert.Equal("Hostile Drifter", shared.VictimText);
        Assert.Equal("Ravnholt, Tessa Korrin, Doro Vanth", shared.MembersText);
        FleetKillViewModel late = fleetKills.Kills[1];
        Assert.Equal("Gila + pod", late.ShipText);
        Assert.Equal("Ravnholt", late.MembersText);

        LossDetailSectionViewModel losses = group.Sections.OfType<LossDetailSectionViewModel>().Single();
        Assert.Equal(["Ravnholt", "Tessa Korrin"], losses.Losses.Select(loss => loss.PilotText));

        // OPEN KILLMAIL opens ET-333 for the mail and the member whose copy it reads.
        shared.OpenCommand.Execute(null);
        Assert.Equal($"killmail-{Own}-1", ((RecordingDialogService)instance.Services.GetRequiredService<EveUtils.Client.Dialogs.IDialogService>())
            .LastKillmailDetail?.ModuleId);

        // The opposite row: a run without a group code has the same kills in its period and shows no such section.
        ActivityDetailViewModel solo = await _LoadAsync(instance, dispatcher, soloRun, group: null);
        Assert.DoesNotContain(solo.Sections, section => section is FleetKillsDetailSectionViewModel);

        _Render(group, "run-info-group");
    }

    [AvaloniaFact]
    public async Task FleetKills_PriceLikeTheKillmailDetail_AndNeverMoveTotalIsk()
    {
        using TestClientInstance instance = _Instance();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        ILocalKillmailRepository store = instance.Services.GetRequiredService<ILocalKillmailRepository>();
        Guid ownRun = await _SaveRunAsync(dispatcher, Own, "Ravnholt", Group, bounty: 2_000_000m);
        await _SaveRunAsync(dispatcher, MateOne, "Tessa Korrin", Group);
        await dispatcher.Send(new RebuildActivitySummariesCommand(), Ct);
        string before = (await _LoadAsync(instance, dispatcher, ownRun)).TotalIskText;

        // A Gila with 3 destroyed repairers and 2 dropped salvage (the drop is not destroyed); a Rifter with an item no
        // price knows. Destroyed: 100M + 3 x 1M, and 2M with the unpriced item adding nothing.
        LocalKillmail gila = _Kill(Own, 1, Gila, StartedAtUtc.AddMinutes(5), Enemy);
        gila.Items.Add(new LocalKillmailItem { CharacterId = Own, KillmailId = 1, Flag = 11, TypeId = Repairer, QuantityDestroyed = 3 });
        gila.Items.Add(new LocalKillmailItem { CharacterId = Own, KillmailId = 1, Flag = 5, TypeId = Salvage, QuantityDropped = 2 });
        LocalKillmail rifter = _Kill(MateOne, 2, Rifter, StartedAtUtc.AddMinutes(6), Enemy + 1);
        rifter.Items.Add(new LocalKillmailItem { CharacterId = MateOne, KillmailId = 2, Flag = 5, TypeId = Unpriced, QuantityDestroyed = 5 });
        await store.AddMissingAsync(Own, [gila]);
        await store.AddMissingAsync(MateOne, [rifter]);
        await instance.Services.GetRequiredService<IMarketPriceRepository>().ReplaceAllAsync(
        [
            _Price(Gila, 100_000_000), _Price(Rifter, 2_000_000), _Price(Repairer, 1_000_000), _Price(Salvage, 10_000_000)
        ], Ct);

        ActivityDetailViewModel after = await _LoadAsync(instance, dispatcher, ownRun);
        FleetKillsDetailSectionViewModel section = after.Sections.OfType<FleetKillsDetailSectionViewModel>().Single();

        var detail = new KillmailDetailViewModel(dispatcher, instance.Services.GetRequiredService<EveUtils.Client.Dialogs.IDialogService>(),
            instance.Services, Own, 1);
        await detail.LoadAsync(Ct);
        Assert.Equal(detail.DestroyedText + " ISK", section.Kills[0].ValueText);
        Assert.Equal(IskFormat.Compact(103_000_000m) + " ISK", section.Kills[0].ValueText);
        Assert.NotEqual(IskFormat.Compact(123_000_000m) + " ISK", section.Kills[0].ValueText);
        Assert.Equal(IskFormat.Compact(2_000_000m) + " ISK", section.Kills[1].ValueText);
        Assert.Equal($"destroyed: {IskFormat.Compact(105_000_000m)} ISK", section.HeaderSummary);

        // The opposite figure: 105M destroyed, and TOTAL ISK is exactly what it was before any kill existed.
        Assert.Equal(before, after.TotalIskText);
        Assert.Null(RunSectionModules.All.Single(module => module.Id == RunSectionId.FleetKills).IskSource);
    }

    private static TestClientInstance _Instance() =>
        TestClientInstance.Create(services =>
        {
            services.AddSingleton<EveUtils.Client.Dialogs.IDialogService>(new RecordingDialogService());
            services.AddSingleton<IExternalCharacterLookup>(new FakeExternalLookup { [Enemy] = "Hostile Drifter", [Enemy + 1] = "Kara Voss" });
            services.AddSingleton<ISdeAccessor>(new FakeSdeAccessor()
                .Add(Gila, "Gila", 26, 6).Add(Rifter, "Rifter", 25, 6).Add(Capsule, "Capsule", 29, 6)
                .Add(Repairer, "Large Armor Repairer", 62, 7).Add(Salvage, "Salvage Drone", 100, 18).Add(Unpriced, "Odd Charge", 86, 8)
                .AddSolarSystem(new SdeSolarSystem(Jita, "Jita", 0.946, "The Forge")));
        });

    private static async Task<Guid> _SaveRunAsync(IDispatcher dispatcher, int characterId, string name, string? group,
        DateTime? startedAtUtc = null, decimal bounty = 0)
    {
        DateTime start = startedAtUtc ?? StartedAtUtc;
        var started = await dispatcher.Send(new StartRunCommand(characterId, ActivityKind.Site, start, 1234, "Angel Hideaway", Jita,
            group, CharacterNameSnapshot: name), Ct);
        await dispatcher.Send(new SaveRunCommand(started.Value, start.AddMinutes(20), start.AddMinutes(21), [],
            bounty > 0 ? [new RunBountyEntryInput { OccurredAtUtc = start.AddMinutes(3), Isk = bounty }] : [], [], []), Ct);
        return started.Value;
    }

    private static async Task<ActivityDetailViewModel> _LoadAsync(TestClientInstance instance, IDispatcher dispatcher, Guid runId,
        string? group = Group)
    {
        IReadOnlyList<ActivityOverviewRowDto> rows = (await dispatcher.Query(new GetActivityOverviewQuery(), Ct)).Value ?? [];
        ActivityOverviewRowDto row = rows.Single(candidate => candidate.RunId == runId || (candidate.GroupCode is not null && candidate.GroupCode == group));
        Dictionary<long, string> names = new() { [Own] = "Ravnholt", [MateOne] = "Tessa Korrin", [MateTwo] = "Doro Vanth" };
        var viewModel = new ActivityDetailViewModel(dispatcher, row.ActivitySummaryId, sde: instance.Services.GetRequiredService<ISdeAccessor>(),
            nameOf: id => names.GetValueOrDefault(id, $"character {id}"), ownCharacterIds: new HashSet<long> { Own },
            dialogs: instance.Services.GetRequiredService<EveUtils.Client.Dialogs.IDialogService>(), services: instance.Services);
        await viewModel.LoadAsync(Ct);
        return viewModel;
    }

    private static LocalKillmail _Kill(int characterId, int killmailId, int shipTypeId, DateTime timeUtc, int victim)
    {
        LocalKillmail kill = _Mail(characterId, killmailId, shipTypeId, timeUtc, victim, isLoss: false);
        kill.Attackers.Add(new LocalKillmailAttacker
        {
            CharacterId = characterId, KillmailId = killmailId, Ordinal = 0, AttackerCharacterId = characterId, ShipTypeId = Rifter,
            DamageDone = 100, FinalBlow = true
        });
        return kill;
    }

    private static LocalKillmail _Loss(int characterId, int killmailId, int shipTypeId, DateTime timeUtc, Guid runId)
    {
        LocalKillmail loss = _Mail(characterId, killmailId, shipTypeId, timeUtc, characterId, isLoss: true);
        loss.RunId = runId;
        loss.LinkSource = KillmailLinkSource.Auto;
        return loss;
    }

    private static LocalKillmail _Mail(int characterId, int killmailId, int shipTypeId, DateTime timeUtc, int victim, bool isLoss) => new()
    {
        CharacterId = characterId, KillmailId = killmailId, Hash = new string('a', 40), KillmailTimeUtc = timeUtc, SolarSystemId = Jita,
        IsLoss = isLoss, VictimShipTypeId = shipTypeId, VictimCharacterId = victim, ImportedAtUtc = DateTime.UtcNow
    };

    private static LocalMarketPrice _Price(int typeId, double price) =>
        new() { TypeId = typeId, AveragePrice = price, AdjustedPrice = price, UpdatedAt = DateTimeOffset.UtcNow };

    private static void _Render(ActivityDetailViewModel viewModel, string name)
    {
        if (ShotsDirectory is not { } directory)
        {
            return;
        }

        var window = new ActivityDetailWindow(viewModel) { Width = 900, Height = 1560 };
        window.Show();
        Directory.CreateDirectory(directory);
        for (int i = 0; i < 16; i++)
        {
            UiDispatcher.UIThread.RunJobs();
        }

        window.UpdateLayout();
        window.CaptureRenderedFrame()?.Save(Path.Combine(directory, $"{name}.png"), new PngBitmapEncoderOptions());
        window.Close();
    }
}
