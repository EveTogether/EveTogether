using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EveUtils.Client.Dialogs;
using EveUtils.Client.ViewModels.Killmails;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Shared.Data;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Esi;
using EveUtils.Shared.Modules.Fittings.Dtos;
using EveUtils.Shared.Modules.Fittings.Repositories;
using EveUtils.Shared.Modules.Killmails;
using EveUtils.Shared.Modules.Killmails.Dtos;
using EveUtils.Shared.Modules.Killmails.Entities;
using EveUtils.Shared.Modules.Killmails.Queries;
using EveUtils.Shared.Modules.Killmails.Repositories;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using IDispatcher = EveUtils.Shared.Cqrs.IDispatcher;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-333: the detail screen of one killmail — the fit lists items under their flag with destroyed/dropped split
/// two-ways, the header value never counts an unpriced item as zero, the fit reconstruction follows the flag table
/// exactly, OPEN FIT is read-only, attackers sort by damage with independent FINAL BLOW/TOP DAMAGE badges, LINKED RUN
/// resolves the same activity the rebuild groups by, the module id is stable for tab dedup, the screen never asks
/// ESI for anything the SDE already has, and both entry points open the same screen. One test per acceptance criterion.
/// </summary>
public sealed class KillmailDetailTests
{
    private const int Pilot = 90000001;
    private const int System1 = 30000142; // Jita
    private const int Gila = 20125;
    private const int ModuleTypeId = 1_900_000_001;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Criterion 1. Red if the split lines land on one row, or in the wrong group (e.g. a charge under
    /// CARGO while it was loaded).</summary>
    [Fact]
    public async Task FitGroups_SplitAPartlyDestroyedPartlyDroppedStackIntoTwoLinesInTheirOwnGroup()
    {
        using TestClientInstance instance = _NewInstance();
        await _PriceAsync(instance, (ModuleTypeId, 1_000_000));
        await _AddAsync(instance, _LossWithItems(1,
            new LocalKillmailItem { Flag = 27, TypeId = ModuleTypeId, QuantityDestroyed = 2, QuantityDropped = 1 })); // HiSlot0

        KillmailDetailViewModel viewModel = await _LoadDetailAsync(instance, Pilot, 1);

        KillmailFitGroupViewModel highSlots = viewModel.FitGroups.Single(group => group.Header == "HIGH SLOTS");
        Assert.Equal(2, highSlots.Rows.Count);
        Assert.Contains(highSlots.Rows, row => row.IsDestroyed && row.QuantityText == "2" && row.BadgeText == "DESTROYED");
        Assert.Contains(highSlots.Rows, row => !row.IsDestroyed && row.QuantityText == "1" && row.BadgeText == "DROPPED");
    }

    /// <summary>Criterion 2. Red if a frozen value or a 0 ISK figure appears, or the total is not the sum of what is
    /// actually priced.</summary>
    [Theory]
    [InlineData(true, true, "3M")]      // ship 1M + item 2M, both priced
    [InlineData(true, false, "1M")]     // the unpriced item counts as nothing, never as 0
    [InlineData(false, false, "no price")]
    public async Task PrimaryFigure_SumsWhatIsPriced_NeverCountingAnUnpricedLineAsZero(
        bool priceShip, bool priceItem, string expected)
    {
        using TestClientInstance instance = _NewInstance();
        if (priceShip)
        {
            await _PriceAsync(instance, (Gila, 1_000_000));
        }

        if (priceItem)
        {
            await _PriceAsync(instance, (ModuleTypeId, 2_000_000));
        }

        await _AddAsync(instance, _LossWithItems(1, new LocalKillmailItem { Flag = 27, TypeId = ModuleTypeId, QuantityDestroyed = 1 }));

        KillmailDetailViewModel viewModel = await _LoadDetailAsync(instance, Pilot, 1);

        Assert.Equal(expected, viewModel.PrimaryFigureText);
    }

    /// <summary>Criterion 3. Red if a charge becomes its own slot, an implant lands in the fit, or a second stack
    /// overwrites the first.</summary>
    [Fact]
    public void Build_PutsEachModuleAndItsLoadedChargeOnTheirFlag_ExcludingNestedImplantsBoostersAndUnknownFlags()
    {
        LocalKillmail killmail = new()
        {
            CharacterId = Pilot, KillmailId = 1, Hash = "h", VictimShipTypeId = Gila,
            Items =
            [
                new LocalKillmailItem { Flag = 27, TypeId = 100, QuantityDestroyed = 1 },                     // HiSlot0 module
                new LocalKillmailItem { Flag = 27, TypeId = 200, QuantityDestroyed = 80 },                    // HiSlot0 loaded charge
                new LocalKillmailItem { Flag = 87, TypeId = 300, QuantityDestroyed = 1, QuantityDropped = 1 }, // DroneBay, two stacks summed
                new LocalKillmailItem { Flag = 5, TypeId = 400, QuantityDropped = 3 },                        // Cargo
                new LocalKillmailItem { Flag = 27, TypeId = 500, QuantityDestroyed = 1, IsNested = true },     // nested — excluded
                new LocalKillmailItem { Flag = 89, TypeId = 600, QuantityDestroyed = 1 },                      // Implant — excluded
                new LocalKillmailItem { Flag = 999, TypeId = 700, QuantityDestroyed = 1 }                      // unknown flag — excluded
            ]
        };

        EsiFitting fit = KillmailFitBuilder.Build(killmail, "test fit");

        Assert.Equal(4, fit.Items.Count);
        Assert.Contains(fit.Items, item => item.TypeId == 100 && item.Flag == "HiSlot0" && item.Quantity == 1);
        Assert.Contains(fit.Items, item => item.TypeId == 200 && item.Flag == "HiSlot0" && item.Quantity == 80);
        Assert.Contains(fit.Items, item => item.TypeId == 300 && item.Flag == "DroneBay" && item.Quantity == 2);
        Assert.Contains(fit.Items, item => item.TypeId == 400 && item.Flag == "Cargo" && item.Quantity == 3);
        Assert.DoesNotContain(fit.Items, item => item.TypeId is 500 or 600 or 700);
    }

    /// <summary>Criterion 4. Red if the number of stored fits changes.</summary>
    [Fact]
    public async Task OpenFit_OpensFitDetailReadOnly_WithoutAddingToTheLibrary()
    {
        using TestClientInstance instance = _NewInstance();
        await _AddAsync(instance, _LossWithItems(1, new LocalKillmailItem { Flag = 27, TypeId = ModuleTypeId, QuantityDestroyed = 1 }));
        IFittingRepository fittings = instance.Services.GetRequiredService<IFittingRepository>();
        int before = (await fittings.ListAllAsync(Ct)).Count;
        KillmailDetailViewModel viewModel = await _LoadDetailAsync(instance, Pilot, 1);
        var dialogs = (RecordingDialogService)instance.Services.GetRequiredService<IDialogService>();

        await viewModel.OpenFitCommand.ExecuteAsync(null);

        Assert.NotNull(dialogs.LastFitDetail);
        Assert.Equal("fit-detail:esi:-1", dialogs.LastFitDetail!.ModuleId); // no local id: read-only
        Assert.Equal(before, (await fittings.ListAllAsync(Ct)).Count);
    }

    /// <summary>Criterion 5. Red if TOP DAMAGE always lands on the final blow.</summary>
    [Fact]
    public async Task Attackers_SortByDamage_WithFinalBlowAndTopDamageOnDifferentRows_AndYouOnTheOwnCharacter()
    {
        using TestClientInstance instance = _NewInstance();
        await _AddAsync(instance, _KillWithAttackers(1,
            new LocalKillmailAttacker { Ordinal = 0, AttackerCharacterId = 100, DamageDone = 4950, FinalBlow = false },
            new LocalKillmailAttacker { Ordinal = 1, AttackerCharacterId = 200, DamageDone = 3120, FinalBlow = true },
            new LocalKillmailAttacker { Ordinal = 2, AttackerCharacterId = Pilot, DamageDone = 2310, FinalBlow = false }));

        KillmailDetailViewModel viewModel = await _LoadDetailAsync(instance, Pilot, 1);

        Assert.True(viewModel.Attackers[0].IsTopDamage);
        Assert.False(viewModel.Attackers[0].IsFinalBlow);
        Assert.True(viewModel.Attackers[1].IsFinalBlow);
        Assert.False(viewModel.Attackers[1].IsTopDamage);
        Assert.True(viewModel.Attackers[2].IsYou);
    }

    /// <summary>Criterion 6. Red if OPEN RUN on a grouped run would reach the wrong summary — the named risk: this
    /// checks the exact key <c>RebuildActivitySummariesCommandHandler</c> groups activities by (GroupCode, or the run
    /// itself when it has none) resolves to the same summary the rebuild actually produced.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("group-1")]
    public async Task LinkedRun_ResolvesTheSameActivitySummaryTheRebuildGroupsRunsBy(string? groupCode)
    {
        using TestClientInstance instance = _NewInstance();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        DateTime startedAtUtc = DateTime.UtcNow.AddMinutes(-30);
        Result<Guid> started = await dispatcher.Send(
            new StartRunCommand(Pilot, ActivityKind.Site, startedAtUtc, 0, null, System1, GroupCode: groupCode), Ct);
        Assert.True((await dispatcher.Send(
            new SaveRunCommand(started.Value, startedAtUtc.AddMinutes(20), startedAtUtc.AddMinutes(21), [], [], [], []), Ct)).IsSuccess);
        await _AddAsync(instance, _Loss(1, started.Value));

        Result<KillmailDetailDto> result = await dispatcher.Query(new GetKillmailDetailQuery(Pilot, 1), Ct);

        await using ClientDbContext db = await instance.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(Ct);
        ActivitySummary expected = groupCode is null
            ? await db.Set<ActivitySummary>().SingleAsync(summary => summary.RunId == started.Value, Ct)
            : await db.Set<ActivitySummary>().SingleAsync(summary => summary.GroupCode == groupCode, Ct);
        Assert.Equal(expected.Id, result.Value!.LinkedRun!.ActivitySummaryId);
    }

    /// <summary>Criterion 7. Red if two ids give the same module id, or the same ids give two different ones — either
    /// would break the single-tab guarantee <c>ModuleHostService.Open</c> relies on.</summary>
    [Theory]
    [InlineData(90000001, 555, "killmail-90000001-555")]
    [InlineData(90000002, 1, "killmail-90000002-1")]
    public void ModuleId_IsDeterministicFromCharacterAndKillmail(int characterId, int killmailId, string expected)
    {
        IServiceProvider services = new ServiceCollection()
            .AddSingleton<EveUtils.Shared.Modules.Sde.ISdeAccessor>(new FakeSdeAccessor())
            .BuildServiceProvider();
        var viewModel = new KillmailDetailViewModel(dispatcher: null!, dialogs: null!, services, characterId, killmailId);

        Assert.Equal(expected, viewModel.ModuleId);
    }

    /// <summary>Criterion 8. Red if a stub resolver is asked again on a second, independent open of the same mail —
    /// types, ships, systems and NPC corps/factions never reach this screen's ESI-side dependencies at all, since
    /// nothing in it calls <c>IEsiClient</c> or the affiliation resolver for anything but a missing player name.</summary>
    [Fact]
    public async Task LoadAsync_AsksForAnUnknownPlayerNameOnce_AndNeverAgainOnASecondOpen()
    {
        var affiliation = new _CountingAffiliationResolver();
        using TestClientInstance instance = _NewInstance(services => services.AddSingleton<IEsiAffiliationResolver>(affiliation));
        await instance.Services.GetRequiredService<ICharacterRegistry>().AddOrUpdateAsync(new Character("Test Pilot", Pilot), Ct);
        await _AddAsync(instance, _LossWithUnknownVictimCorp(1, corporationId: 98765432));

        await _LoadDetailAsync(instance, Pilot, 1);
        int callsAfterFirstOpen = affiliation.CorporationCalls;
        await _LoadDetailAsync(instance, Pilot, 1); // a second, independent open of the same mail

        Assert.True(callsAfterFirstOpen > 0); // the unknown corp really was resolved once
        Assert.Equal(callsAfterFirstOpen, affiliation.CorporationCalls); // never asked again
    }

    /// <summary>Criterion 9. Red if either entry point does nothing.</summary>
    public static IEnumerable<object[]> EntryPoints()
    {
        yield return
        [
            (Func<Task<bool>>)(async () =>
            {
                using TestClientInstance instance = _NewInstance();
                await instance.Services.GetRequiredService<ICharacterRegistry>().AddOrUpdateAsync(new Character("Test Pilot", Pilot), Ct);
                await _AddAsync(instance, _KillWithAttackers(1));
                IReadOnlyList<Character> characters = await instance.Services.GetRequiredService<ICharacterRegistry>().GetAllAsync(Ct);
                var dialogs = (RecordingDialogService)instance.Services.GetRequiredService<IDialogService>();
                var overview = new KillmailsOverviewViewModel(instance.Services.GetRequiredService<IDispatcher>(), dialogs,
                    instance.Services, characters, (_, _) => Task.CompletedTask);
                await overview.LoadAsync(Ct);
                overview.Days.Single().Rows.Single().OpenCommand.Execute(null);
                return dialogs.LastKillmailDetail is not null;
            })
        ];
        yield return
        [
            (Func<Task<bool>>)(() =>
            {
                bool opened = false;
                var linkedLoss = new LinkedLossViewModel(dispatcher: null!, Pilot, 1, [], () => Task.CompletedTask, () => opened = true)
                {
                    ShipText = "Gila", FitText = "fit", TimeText = "t", FinalBlowText = "fb", ReasonText = "r"
                };
                linkedLoss.OpenKillmailCommand.Execute(null);
                return Task.FromResult(opened);
            })
        ];
    }

    [Theory]
    [MemberData(nameof(EntryPoints))]
    public async Task OpenKillmail_WorksFromBothTheKillmailsRowAndTheLinkedLossButton(Func<Task<bool>> scenario) =>
        Assert.True(await scenario());

    private static TestClientInstance _NewInstance(Action<IServiceCollection>? extra = null) =>
        TestClientInstance.Create(services =>
        {
            services.AddSingleton<IDialogService>(new RecordingDialogService());
            extra?.Invoke(services);
        });

    private static async Task<KillmailDetailViewModel> _LoadDetailAsync(TestClientInstance instance, int characterId, int killmailId)
    {
        await instance.Services.GetRequiredService<ICharacterRegistry>().AddOrUpdateAsync(new Character("Test Pilot", Pilot), Ct);
        var viewModel = new KillmailDetailViewModel(instance.Services.GetRequiredService<IDispatcher>(),
            instance.Services.GetRequiredService<IDialogService>(), instance.Services, characterId, killmailId);
        await viewModel.LoadAsync(Ct);
        return viewModel;
    }

    private static LocalKillmail _LossWithItems(int killmailId, params LocalKillmailItem[] items)
    {
        LocalKillmail killmail = new()
        {
            CharacterId = Pilot, KillmailId = killmailId, Hash = $"hash{killmailId}", KillmailTimeUtc = DateTime.UtcNow,
            SolarSystemId = System1, IsLoss = true, VictimShipTypeId = Gila, VictimCharacterId = Pilot,
            LinkSource = KillmailLinkSource.None, ImportedAtUtc = DateTime.UtcNow
        };
        foreach (LocalKillmailItem item in items)
        {
            item.CharacterId = Pilot;
            item.KillmailId = killmailId;
            killmail.Items.Add(item);
        }

        return killmail;
    }

    private static LocalKillmail _LossWithUnknownVictimCorp(int killmailId, int corporationId) => new()
    {
        CharacterId = Pilot, KillmailId = killmailId, Hash = $"hash{killmailId}", KillmailTimeUtc = DateTime.UtcNow,
        SolarSystemId = System1, IsLoss = true, VictimShipTypeId = Gila, VictimCharacterId = null,
        VictimCorporationId = corporationId, LinkSource = KillmailLinkSource.None, ImportedAtUtc = DateTime.UtcNow
    };

    private static LocalKillmail _Loss(int killmailId, Guid linkedRunId) => new()
    {
        CharacterId = Pilot, KillmailId = killmailId, Hash = $"hash{killmailId}", KillmailTimeUtc = DateTime.UtcNow,
        SolarSystemId = System1, IsLoss = true, VictimShipTypeId = Gila, VictimCharacterId = Pilot,
        RunId = linkedRunId, LinkSource = KillmailLinkSource.Auto, ImportedAtUtc = DateTime.UtcNow
    };

    private static LocalKillmail _KillWithAttackers(int killmailId, params LocalKillmailAttacker[] attackers)
    {
        LocalKillmail killmail = new()
        {
            CharacterId = Pilot, KillmailId = killmailId, Hash = $"hash{killmailId}", KillmailTimeUtc = DateTime.UtcNow,
            SolarSystemId = System1, IsLoss = false, VictimShipTypeId = Gila, VictimCharacterId = Pilot + 999,
            LinkSource = KillmailLinkSource.None, ImportedAtUtc = DateTime.UtcNow
        };
        foreach (LocalKillmailAttacker attacker in attackers)
        {
            attacker.CharacterId = Pilot;
            attacker.KillmailId = killmailId;
            killmail.Attackers.Add(attacker);
        }

        return killmail;
    }

    private static Task _AddAsync(TestClientInstance instance, params LocalKillmail[] killmails) =>
        instance.Services.GetRequiredService<ILocalKillmailRepository>().AddMissingAsync(Pilot, killmails, Ct);

    private static Task _PriceAsync(TestClientInstance instance, params (int TypeId, double Price)[] prices) =>
        instance.Services.GetRequiredService<EveUtils.Shared.Modules.Market.Repositories.IMarketPriceRepository>().ReplaceAllAsync(
        [
            .. prices.Select(price => new EveUtils.Shared.Modules.Market.Entities.LocalMarketPrice
            {
                TypeId = price.TypeId, AveragePrice = price.Price, AdjustedPrice = price.Price, UpdatedAt = DateTimeOffset.UtcNow
            })
        ], Ct);

    private sealed class _CountingAffiliationResolver : IEsiAffiliationResolver
    {
        public int CorporationCalls { get; private set; }

        public Task<EsiCharacterAffiliation?> ResolveAsync(int characterId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string?> ResolveCorporationNameAsync(int corporationId, CancellationToken cancellationToken = default)
        {
            CorporationCalls++;
            return Task.FromResult<string?>("Some Corp");
        }

        public Task<string?> ResolveAllianceNameAsync(int allianceId, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);
    }
}
