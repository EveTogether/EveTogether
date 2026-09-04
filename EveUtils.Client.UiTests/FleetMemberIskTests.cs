using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using EveUtils.Client.Fleet;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Modules.Fleet.Metrics;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// What each member of the fleet has made, under the FLEET fold, with the fleet's own total under the names.
///
/// Loot and bounty travel the 1 Hz metric stream every other fleet figure travels, as their own kinds, and both are
/// opt-IN at the share gate like the bounty figure already was: what a pilot made is theirs to offer. The loot ISK
/// is the one the LOOT section already shows — priced from the market cache by type id, never from the clipboard's
/// own ISK column — so no figure is valued twice and a member's row says what their own window says.
/// </summary>
public sealed class FleetMemberIskTests
{
    private const long FleetId = 4242;
    private const int Pilot = 90000001;
    private const int Other = 90000002;

    /// <summary>
    /// The whole chain, from the window that priced the loot to the row that shows it: the run window hands the
    /// figure to the metric source, the publisher's tick polls that source, and the sample comes back off the bus
    /// onto a member row. Nothing is called directly — a source nothing polls, or a window that never hands its
    /// figure over, is exactly the failure this asks about.
    ///
    /// The bounty on the same row is the one the gamelog has been putting on this stream all along; what is new is
    /// that the window keeps it instead of dropping every sample that was not a location.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(true, 1000)]    // a run on the clock, with a priced loot figure
    [InlineData(true, null)]    // …and one whose loot the price cache could value nothing of
    [InlineData(false, null)]   // no run at all: nothing to offer, and nothing offered
    public async Task WhatARunHasLooted_TravelsTheFleetStreamOntoTheMemberRow(bool onTheClock, int? lootIsk)
    {
        using TestClientInstance instance = await _InstanceAsync();
        using var window = new ActivityWindowViewModel(ActivityKind.Site, instance.Services);
        await window.LoadAsync();

        // A location as well, so the row is there in every case and the ISK line is the only thing that varies.
        await _ShareLocationAsync(instance, Pilot, "Shaggoth");

        if (onTheClock)
        {
            window.StartManualRun(DateTime.UtcNow);
            window.RunLoot!.NetIsk = lootIsk;
        }

        window.Refresh(DateTime.UtcNow);
        await instance.Services.GetRequiredService<FleetMetricPublisher>()
            .PublishTickAsync(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Dispatcher.UIThread.RunJobs();

        ActivityFleetMemberViewModel member = Assert.Single(window.FleetMembers);
        Assert.Equal(lootIsk, member.LootIsk);
        Assert.NotNull(member.BountyIsk);

        // A figure nobody offered says so; it is never a zero, which would read as "they found nothing".
        if (lootIsk is null)
            Assert.Contains("loot not shared", member.IskText);
    }

    /// <summary>
    /// The total is exactly the sum of the rows standing above it — the two are the same set on purpose, so the
    /// figure needs no sentence explaining what it covers. A member sharing nothing is in neither, and the caption
    /// under both is what says the fleet may be larger than this list.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(1000, 250, 500, 100, true)]     // both members share both figures
    // One shares loot and the other bounty, so the two halves of the total come from different rows — and that
    // second member shares no location at all, which is the ordinary case: the three are separate opt-ins. Sharing
    // a figure is being heard from, so it puts them in the list, or the total would exceed the names above it.
    [InlineData(1000, null, null, 250, false)]
    [InlineData(null, null, null, null, true)]  // neither shares a figure; both are here by their location alone
    public async Task TheFleetTotal_IsExactlyTheSumOfTheRowsAboveIt(
        int? pilotLoot, int? pilotBounty, int? otherLoot, int? otherBounty, bool otherSharesLocation)
    {
        using TestClientInstance instance = await _InstanceAsync();
        using var window = new ActivityWindowViewModel(ActivityKind.Site, instance.Services);
        await window.LoadAsync();

        await _ShareLocationAsync(instance, Pilot, "Shaggoth");
        if (otherSharesLocation)
            await _ShareLocationAsync(instance, Other, "Amarr");
        await _ShareIskAsync(instance, Pilot, pilotLoot, pilotBounty);
        await _ShareIskAsync(instance, Other, otherLoot, otherBounty);

        Assert.Equal(2, window.FleetMembers.Count);
        Assert.True(window.IsFleetTotalShown);

        decimal? loot = _Sum(pilotLoot, otherLoot);
        decimal? bounty = _Sum(pilotBounty, otherBounty);

        // The invariant, stated over the rows themselves rather than over the numbers that were published.
        Assert.Equal(loot, _SumOfRows(window, member => member.LootIsk));
        Assert.Equal(bounty, _SumOfRows(window, member => member.BountyIsk));

        if (loot is null && bounty is null)
        {
            Assert.Equal("no member is sharing loot or bounty", window.FleetTotalText);
            return;
        }

        Assert.Contains(
            loot is { } lootTotal ? ActivityFleetMemberViewModel.Isk(lootTotal) : "loot not shared",
            window.FleetTotalText);
        Assert.Contains(
            bounty is { } bountyTotal ? ActivityFleetMemberViewModel.Isk(bountyTotal) : "bounty not shared",
            window.FleetTotalText);
    }

    private static decimal? _Sum(int? first, int? second) =>
        first is null && second is null ? null : (first ?? 0) + (second ?? 0);

    private static decimal? _SumOfRows(
        ActivityWindowViewModel window, Func<ActivityFleetMemberViewModel, decimal?> figure) =>
        window.FleetMembers.Select(figure).OfType<decimal>().ToList() is { Count: > 0 } shared
            ? shared.Sum()
            : null;

    private static async Task<TestClientInstance> _InstanceAsync()
    {
        TestClientInstance instance = TestClientInstance.Create(services =>
            services.AddSingleton<IExternalCharacterLookup>(
                new FakeExternalLookup { [Pilot] = "RaymondKrah", [Other] = "Jithran" }));
        await instance.Services.GetRequiredService<ICharacterRegistry>()
            .AddOrUpdateAsync(new Character("RaymondKrah", Pilot));
        instance.Services.GetRequiredService<IFleetParticipation>()
            .Set([new FleetParticipant(Pilot, FleetId, ClientOnly: true, Pilot)]);
        return instance;
    }

    private static Task _ShareLocationAsync(TestClientInstance instance, int characterId, string system) =>
        _PublishAsync(instance, new MetricSample(characterId, FleetId, MetricKind.Location, 0, 1_000, system));

    private static async Task _ShareIskAsync(TestClientInstance instance, int characterId, int? loot, int? bounty)
    {
        if (loot is { } lootIsk)
            await _PublishAsync(instance, new MetricSample(characterId, FleetId, MetricKind.Loot, lootIsk, 1_000));
        if (bounty is { } bountyIsk)
            await _PublishAsync(instance, new MetricSample(characterId, FleetId, MetricKind.Bounty, bountyIsk, 1_000));
    }

    private static async Task _PublishAsync(TestClientInstance instance, MetricSample sample)
    {
        await instance.Services.GetRequiredService<IEventBus>()
            .PublishAsync(new FleetMetricEvent(sample, sample.CharacterId));
        Dispatcher.UIThread.RunJobs();
    }
}
