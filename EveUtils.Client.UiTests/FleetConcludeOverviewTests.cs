using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Fleet;
using EveUtils.Client.ViewModels;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Fleet.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-407: concluding a started fleet from the overview's STOP dialog must land it in the FINISHED band — not back
/// in standing by, and not nowhere.
/// </summary>
public class FleetConcludeOverviewTests
{
    private const int Owner = 95400001;

    [AvaloniaFact]
    public async Task Conclude_FromStopDialog_MovesTheStartedFleetToFinished()
    {
        var dialogs = new RecordingDialogService { FleetExit = StopFleetChoice.Conclude };
        using var instance = TestClientInstance.Create(services => services.AddSingleton<IDialogService>(dialogs));
        var services = instance.Services;
        await services.GetRequiredService<ICharacterRegistry>().AddOrUpdateAsync(new Character("Jithran", Owner));

        var fleetService = services.GetRequiredService<ClientFleetService>();
        var created = await fleetService.CreateLocalFleetAsync("Conclude overview", null, Owner);
        Assert.True(created.IsSuccess);
        var local = new LocalFleetClient(fleetService, services.GetRequiredService<IFleetRepository>(),
            services.GetRequiredService<ICharacterRegistry>(), Owner);
        Assert.True((await local.StartFleetAsync(created.Value)).Ok);

        using var vm = new FleetsViewModel(services, runClock: false);
        await WaitForAsync(() => vm.ActiveFleets.Count == 1);
        Assert.Empty(vm.FinishedFleets);

        await vm.StopRowCommand.ExecuteAsync(vm.ActiveFleets[0]);
        await WaitForAsync(() => vm.FinishedFleets.Count == 1);

        Assert.Empty(vm.ActiveFleets);
        Assert.Empty(vm.StandingByFleets);
        Assert.Equal("1 fleet", vm.FinishedSummaryText);
    }

    private static async Task WaitForAsync(System.Func<bool> condition)
    {
        for (var i = 0; i < 300 && !condition(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }

        Dispatcher.UIThread.RunJobs();
    }
}
