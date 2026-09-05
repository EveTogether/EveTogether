using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using EveUtils.Client.ViewModels;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>ET-195: "ISK today" must read saved runs from storage, not sum live trackers' lifetime bounty. One
/// counter-proof per acceptance criterion, taken from the ticket itself.</summary>
public sealed class IskTodayTests
{
    private const long CharacterId = 90000001;

    private static async Task<Guid> _SaveRunWithBountyAsync(IDispatcher dispatcher, DateTime startedAtUtc, decimal bountyIsk, long characterId = CharacterId)
    {
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(characterId, ActivityKind.Site, startedAtUtc, 1234, "Homefront", 30000142));
        await dispatcher.Send(new SaveRunCommand(started.Value, startedAtUtc.AddMinutes(15), startedAtUtc.AddMinutes(16), [],
            [new RunBountyEntryInput { OccurredAtUtc = startedAtUtc.AddMinutes(5), Isk = bountyIsk }], [], []));
        await dispatcher.Send(new RebuildActivitySummariesCommand());
        return started.Value;
    }

    /// <summary>AC-1 (also the mandated core counter-proof): a run saved today, with no live tracker at all —
    /// simulated by closing the app and reopening on the data it left behind, per <see cref="TestClientInstance"/>'s
    /// restart contract. Counter-proof: the pre-fix code summed only <c>tracker.Bounty</c>, an in-memory counter that
    /// starts empty on every fresh process — this would read "0" instead of the saved bounty.</summary>
    [Fact]
    public async Task IskToday_SurvivesARestart()
    {
        string instanceName = "uitest-isktoday-" + Guid.NewGuid().ToString("N");
        using (var first = TestClientInstance.Create(instanceName: instanceName))
        {
            first.KeepDataOnDispose = true;
            IDispatcher dispatcher = first.Services.GetRequiredService<IDispatcher>();
            await first.Services.GetRequiredService<ICharacterRegistry>().AddOrUpdateAsync(new Character("Noahmarr", (int)CharacterId), TestContext.Current.CancellationToken);
            await _SaveRunWithBountyAsync(dispatcher, DateTime.UtcNow, 4_200_000m);
        }

        using var restarted = TestClientInstance.Create(instanceName: instanceName);
        var home = new HomeDashboardViewModel(restarted.Services, []);

        await home.RebuildRosterAsync();

        Assert.Equal("4.2M", home.IskTodayText);
    }

    /// <summary>AC-2: a character logs out (its live tracker disappears) but the run it saved today still counts.
    /// Counter-proof: the pre-fix code only summed <see cref="HomeDashboardViewModel.MyCharacters"/>'s trackers, so
    /// removing the tracker would drop this character's bounty from the total.</summary>
    [Fact]
    public async Task IskToday_KeepsCountingAfterItsTrackerIsRemoved()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        await instance.Services.GetRequiredService<ICharacterRegistry>().AddOrUpdateAsync(new Character("Noahmarr", (int)CharacterId), TestContext.Current.CancellationToken);
        await _SaveRunWithBountyAsync(dispatcher, DateTime.UtcNow, 1_500_000m);

        var trackers = new ObservableCollection<DpsViewModel> { new("Noahmarr", isSelf: true) };
        var home = new HomeDashboardViewModel(instance.Services, trackers);
        await home.RebuildRosterAsync();

        trackers.RemoveAt(0); // the character logs out — its tracker goes away
        await home.RebuildRosterAsync();

        Assert.Equal("1.5M", home.IskTodayText);
    }

    /// <summary>AC-3: a run just before the day boundary does not count, one just after does. This is the test that
    /// pins the chosen boundary down — with it removed, any boundary reads as correct.</summary>
    [Fact]
    public async Task IskToday_ExcludesYesterday_IncludesToday()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        await instance.Services.GetRequiredService<ICharacterRegistry>().AddOrUpdateAsync(new Character("Noahmarr", (int)CharacterId), TestContext.Current.CancellationToken);
        DateTime localMidnightUtc = DateTime.Now.Date.ToUniversalTime();
        await _SaveRunWithBountyAsync(dispatcher, localMidnightUtc.AddMinutes(-5), 9_000_000m);  // yesterday
        await _SaveRunWithBountyAsync(dispatcher, localMidnightUtc.AddMinutes(5), 2_000_000m);   // today

        var home = new HomeDashboardViewModel(instance.Services, []);
        await home.RebuildRosterAsync();

        Assert.Equal("2.0M", home.IskTodayText);
    }

    /// <summary>AC-4: a live tracker and a saved run describing the same activity must not both count. The tracker's
    /// bounty is set far apart from the saved amount so a double count (or a fall-back to the tracker's figure)
    /// would show up as neither "2.0M" nor a sum of the two.</summary>
    [Fact]
    public async Task IskToday_DoesNotDoubleCount_LiveTrackerAndItsOwnSavedRun()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        await instance.Services.GetRequiredService<ICharacterRegistry>().AddOrUpdateAsync(new Character("Noahmarr", (int)CharacterId), TestContext.Current.CancellationToken);
        await _SaveRunWithBountyAsync(dispatcher, DateTime.UtcNow, 2_000_000m);

        var trackers = new ObservableCollection<DpsViewModel> { new("Noahmarr", isSelf: true) { Bounty = 50_000_000 } };
        var home = new HomeDashboardViewModel(instance.Services, trackers);

        await home.RebuildRosterAsync();

        Assert.Equal("2.0M", home.IskTodayText);
    }
}
