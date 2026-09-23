using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using EveUtils.Client.Gamelog;
using EveUtils.Client.ViewModels;
using EveUtils.Client.ViewModels.Home;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Location;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>ET-327: the pilot row and the DPS window read the same game log field. A system that field learns after the
/// home is open — from a jump line or from the ESI gap fill — has to reach the row without a restart.</summary>
public sealed class HomePilotLocationTests
{
    private const int CharacterId = 96000002;

    /// <summary>The gap fill sets the location without a game log line, so nothing of the watcher's is raised. Red when the
    /// row only listens to the watcher: it stays on "locating…".</summary>
    [AvaloniaFact]
    public async Task ALocationSetAfterTheHomeOpened_ShowsInThePilotRow()
    {
        using var instance = TestClientInstance.Create();
        GamelogClientService gamelog = instance.Services.GetRequiredService<GamelogClientService>();
        var character = new CharacterViewModel(new Character("Noahmarr", CharacterId, [LocationScopeCatalog.ReadLocation]))
        {
            HasActiveClient = true
        };
        using var pilots = new HomePilotsViewModel(new ObservableCollection<CharacterViewModel> { character },
            instance.Services, HomeNavigation.None);
        HomePilotRowViewModel row = pilots.Rows.Single();
        pilots.Tick(DateTime.UtcNow);
        Assert.Equal(WhereState.Locating, row.Where);

        gamelog.SetLocation("Noahmarr", "Jita", DateTime.UtcNow);
        pilots.Tick(DateTime.UtcNow);
        await _SettleAsync();

        Assert.Equal(WhereState.System, row.Where);
        Assert.Equal("Jita", row.SystemName);
    }

    /// <summary>A jump after the first system moves the row on the next tick, not only the first detection.</summary>
    [AvaloniaFact]
    public async Task AJumpAfterTheFirstSystem_MovesThePilotRow()
    {
        using var instance = TestClientInstance.Create();
        GamelogClientService gamelog = instance.Services.GetRequiredService<GamelogClientService>();
        var character = new CharacterViewModel(new Character("Noahmarr", CharacterId, [LocationScopeCatalog.ReadLocation]))
        {
            HasActiveClient = true
        };
        using var pilots = new HomePilotsViewModel(new ObservableCollection<CharacterViewModel> { character },
            instance.Services, HomeNavigation.None);
        HomePilotRowViewModel row = pilots.Rows.Single();

        gamelog.SetLocation("Noahmarr", "Jita", DateTime.UtcNow);
        pilots.Tick(DateTime.UtcNow);
        gamelog.SetLocation("Noahmarr", "Perimeter", DateTime.UtcNow);
        pilots.Tick(DateTime.UtcNow);
        await _SettleAsync();

        Assert.Equal("Perimeter", row.SystemName);
    }

    private static async Task _SettleAsync()
    {
        for (int settle = 0; settle < 10; settle++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }
}
