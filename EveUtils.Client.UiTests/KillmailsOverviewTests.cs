using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EveUtils.Client.ViewModels.Killmails;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Killmails;
using EveUtils.Shared.Modules.Killmails.Commands;
using EveUtils.Shared.Modules.Killmails.Dtos;
using EveUtils.Shared.Modules.Killmails.Entities;
using EveUtils.Shared.Modules.Killmails.Repositories;
using EveUtils.Shared.Modules.Market.Entities;
using EveUtils.Shared.Modules.Market.Repositories;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using IDispatcher = EveUtils.Shared.Cqrs.IDispatcher;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-332: the KILLMAILS overview — the SHOW filter narrows to what it claims, efficiency guards against an empty
/// or unpriced character, a character without the scope shows GRANT ACCESS instead of an empty list, a search hits
/// ship, pilot and system alike, mails group under their local calendar day newest first, and an unpriced mail reads
/// "no price" rather than 0 ISK. One test per acceptance criterion.
/// </summary>
public sealed class KillmailsOverviewTests
{
    private const int Pilot = 90000001;
    private const int System1 = 30000142; // Jita
    private const int Gila = 20125;
    private const int Vexor = 626;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Criterion 1. Red if Not linked also shows the linked loss or the kill.</summary>
    [Theory]
    [InlineData(KillmailShowFilter.Losses, new[] { 2, 3 })]
    [InlineData(KillmailShowFilter.NotLinked, new[] { 3 })]
    public async Task ShowFilter_NarrowsToWhatItClaims(KillmailShowFilter filter, int[] expectedKillmailIds)
    {
        using TestClientInstance instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        Guid runId = await _SaveRunAsync(dispatcher);
        await _AddAsync(instance, _Kill(1), _Loss(2, linkedRunId: null), _Loss(3, linkedRunId: null));
        await dispatcher.Send(new SetKillmailRunLinkCommand(Pilot, 2, runId), Ct);
        KillmailsOverviewViewModel viewModel = await _LoadAsync(instance, hasScope: true);

        viewModel.Filters.Single(tile => tile.Key == filter).SelectCommand.Execute(null);

        Assert.Equal(expectedKillmailIds.OrderBy(id => id), _VisibleIds(viewModel).OrderBy(id => id));
    }

    /// <summary>Criterion 2. Red if an empty or fully unpriced character shows 0% or throws instead of "—".</summary>
    [Theory]
    [InlineData(false, "—")]
    [InlineData(true, "66.7%")]
    public async Task Efficiency_IsDestroyedOverDestroyedPlusLost_OrADashWhenThereIsNothingToDivide(bool seedPricedMails, string expected)
    {
        using TestClientInstance instance = TestClientInstance.Create();
        if (seedPricedMails)
        {
            IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
            Guid runId = await _SaveRunAsync(dispatcher);
            await _PriceAsync(instance, (Gila, 2_000_000), (Vexor, 1_000_000));
            await _AddAsync(instance, _Kill(1, Gila), _Loss(2, linkedRunId: null, Vexor));
            await dispatcher.Send(new SetKillmailRunLinkCommand(Pilot, 2, runId), Ct);
        }

        KillmailsOverviewViewModel viewModel = await _LoadAsync(instance, hasScope: true);

        Assert.Equal(expected, viewModel.EfficiencyText);
    }

    /// <summary>Criterion 3. Red if a character without the scope shows an empty list instead of GRANT ACCESS. The
    /// contrast — a character that did grant it reads normally — is every other criterion here: all of them load
    /// with <c>hasScope: true</c> and see their seeded mails.</summary>
    [Fact]
    public async Task Character_WithoutTheScope_ShowsGrantAccess_NotAnEmptyList()
    {
        using TestClientInstance instance = TestClientInstance.Create();
        await _AddAsync(instance, _Kill(1)); // present in storage, so an empty Days here can only mean the gate, not a genuinely empty character

        KillmailsOverviewViewModel viewModel = await _LoadAsync(instance, hasScope: false);

        Assert.True(viewModel.NeedsAccess);
        Assert.Empty(viewModel.Days);
        Assert.True(viewModel.Characters.Single().NeedsAccess);
    }

    /// <summary>Criterion 4. Red if only the ship name is searched.</summary>
    [Theory]
    [InlineData("tama", true)]      // the system
    [InlineData("gila", true)]      // the ship
    [InlineData("holloway", true)]  // the pilot
    [InlineData("uedama", false)]   // none of the three
    public void Matches_SearchesShipSystemAndPilot(string needle, bool expected)
    {
        KillmailRowViewModel row = _Row(shipName: "Gila", systemName: "Tama", counterparty: "Dexter Holloway");

        Assert.Equal(expected, row.Matches(needle));
    }

    /// <summary>Criterion 5. Red if a day's mails do not group together, or the newest day is not first.</summary>
    [Fact]
    public async Task Days_GroupMailsByLocalCalendarDay_NewestFirst()
    {
        using TestClientInstance instance = TestClientInstance.Create();
        DateTime laterDay = new(2026, 3, 15, 12, 0, 0, DateTimeKind.Utc);
        DateTime sameLaterDay = laterDay.AddHours(2);
        DateTime earlierDay = laterDay.AddDays(-3); // a gap no real-world UTC offset (±14h) can fold into the same local day
        await _AddAsync(instance, _Kill(1, atUtc: laterDay), _Kill(2, atUtc: sameLaterDay), _Kill(3, atUtc: earlierDay));

        KillmailsOverviewViewModel viewModel = await _LoadAsync(instance, hasScope: true);

        Assert.Equal(2, viewModel.Days.Count);
        Assert.True(viewModel.Days[0].Day > viewModel.Days[1].Day);
        Assert.Equal(2, viewModel.Days[0].Rows.Count);
        Assert.Single(viewModel.Days[1].Rows);
    }

    /// <summary>Criterion 6. Red if an unpriced mail shows 0 ISK instead of "no price".</summary>
    [Theory]
    [InlineData(null, "no price")]
    [InlineData(1_000_000L, "1M")]
    public void IskText_IsNoPrice_NeverAZeroFigure_WhenNothingIsPriced(long? iskValue, string expected)
    {
        KillmailRowViewModel row = _Row(iskValue: iskValue is { } value ? value : null);

        Assert.Equal(expected, row.IskText);
    }

    private static async Task<KillmailsOverviewViewModel> _LoadAsync(TestClientInstance instance, bool hasScope)
    {
        Character[] characters = [new Character("Test Pilot", Pilot,
            GrantedScopes: hasScope ? [KillmailsScopeCatalog.ReadKillmails] : [])];
        KillmailsOverviewViewModel viewModel = new(instance.Services.GetRequiredService<IDispatcher>(),
            instance.Services, characters, (_, _) => Task.CompletedTask);
        await viewModel.LoadAsync(Ct);
        return viewModel;
    }

    private static IEnumerable<int> _VisibleIds(KillmailsOverviewViewModel viewModel) =>
        viewModel.Days.SelectMany(day => day.Rows).Select(row => row.KillmailId);

    private static LocalKillmail _Kill(int killmailId, int shipTypeId = Gila, DateTime? atUtc = null) => new()
    {
        CharacterId = Pilot,
        KillmailId = killmailId,
        Hash = $"hash{killmailId}",
        KillmailTimeUtc = atUtc ?? DateTime.UtcNow,
        SolarSystemId = System1,
        IsLoss = false,
        VictimShipTypeId = shipTypeId,
        VictimCharacterId = Pilot + 1000,
        LinkSource = KillmailLinkSource.None,
        ImportedAtUtc = DateTime.UtcNow,
        Attackers = [new LocalKillmailAttacker { CharacterId = Pilot, KillmailId = killmailId, Ordinal = 0, AttackerCharacterId = Pilot, FinalBlow = true, DamageDone = 100 }]
    };

    private static LocalKillmail _Loss(int killmailId, Guid? linkedRunId, int shipTypeId = Vexor) => new()
    {
        CharacterId = Pilot,
        KillmailId = killmailId,
        Hash = $"hash{killmailId}",
        KillmailTimeUtc = DateTime.UtcNow,
        SolarSystemId = System1,
        IsLoss = true,
        VictimShipTypeId = shipTypeId,
        VictimCharacterId = Pilot,
        RunId = linkedRunId,
        LinkSource = linkedRunId is null ? KillmailLinkSource.None : KillmailLinkSource.Auto,
        ImportedAtUtc = DateTime.UtcNow
    };

    private static async Task<Guid> _SaveRunAsync(IDispatcher dispatcher)
    {
        DateTime startedAtUtc = DateTime.UtcNow.AddMinutes(-30);
        Result<Guid> started = await dispatcher.Send(
            new StartRunCommand(Pilot, ActivityKind.Site, startedAtUtc, 0, null, System1), Ct);
        Result saved = await dispatcher.Send(
            new SaveRunCommand(started.Value, startedAtUtc.AddMinutes(20), startedAtUtc.AddMinutes(21), [], [], [], []), Ct);
        Assert.True(saved.IsSuccess);
        return started.Value;
    }

    private static Task _AddAsync(TestClientInstance instance, params LocalKillmail[] killmails) =>
        instance.Services.GetRequiredService<ILocalKillmailRepository>().AddMissingAsync(Pilot, killmails, Ct);

    private static Task _PriceAsync(TestClientInstance instance, params (int TypeId, double Price)[] prices) =>
        instance.Services.GetRequiredService<IMarketPriceRepository>().ReplaceAllAsync(
        [
            .. prices.Select(price => new LocalMarketPrice
            {
                TypeId = price.TypeId, AveragePrice = price.Price, AdjustedPrice = price.Price, UpdatedAt = DateTimeOffset.UtcNow
            })
        ], Ct);

    private static KillmailRowViewModel _Row(string shipName = "Gila", string systemName = "Tama", string counterparty = "NPC",
        long? iskValue = null)
    {
        KillmailOverviewRowDto dto = new(Pilot, 1, DateTime.UtcNow, System1, IsLoss: false, VictimShipTypeId: Gila,
            VictimCharacterId: null, VictimCorporationId: null, VictimAllianceId: null, AttackerCount: 1, FinalBlow: null,
            RunId: null, LinkSource: KillmailLinkSource.None, NotLinkedCandidateCount: 0,
            IskValue: iskValue is { } value ? value : null);
        return new KillmailRowViewModel(dto, shipName, systemName, regionName: null, isAbyssal: false, securityText: "0.5",
            counterpartyName: counterparty, openDetail: _ => Task.CompletedTask);
    }
}
