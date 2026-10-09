using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Fleet;
using EveUtils.Client.Platform;
using EveUtils.Client.Transport;
using EveUtils.Client.ViewModels;
using EveUtils.Client.Views;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Transport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-491: the NOW ACTIVE cards of one row differed in height — a pilot standing by somewhere carries a foot line, one
/// who is not did not — and a chip per pilot pushed the toolbar onto a second line at six pilots. Measured off the
/// laid-out window, in numbers.
/// </summary>
public class FleetsBandLayoutTests
{
    private const string Server = "srv:7443";
    private const int FirstPilot = 2001;

    /// <summary>Two rows of cards at each width; a third row is the compact band's (ET-170), which has no cards.</summary>
    [AvaloniaTheory]
    [InlineData(6, 1300)]
    [InlineData(8, 1578)]
    public async Task Band_CardsWithAndWithoutTheStandingByFoot_AreAllTheSameHeight(int pilots, double width)
    {
        var (instance, vm) = await SceneAsync(pilots);
        using (instance)
        {
            var window = Show(vm, width);

            List<Rect> cards = [.. window.GetVisualDescendants().OfType<Border>()
                .Where(border => border.Classes.Contains("statuscard") && border.IsEffectivelyVisible)
                .Select(border => border.Bounds)];
            Assert.Equal(pilots, cards.Count);
            Assert.Contains(vm.Lanes, lane => lane.ShowFootChips);
            Assert.Contains(vm.Lanes, lane => !lane.ShowFootChips);
            Assert.Single(cards.Select(card => Math.Round(card.Height, 1)).Distinct());

            window.Close();
            vm.Dispose();
        }
    }

    /// <summary>The usual window (~1300) and the narrow one the toolbar was already measured to fit in (758).</summary>
    [AvaloniaTheory]
    [InlineData(6, 1300)]
    [InlineData(12, 1300)]
    [InlineData(12, 758)]
    public async Task Toolbar_StaysOnOneLine(int pilots, double width)
    {
        var (instance, vm) = await SceneAsync(pilots);
        using (instance)
        {
            var window = Show(vm, width);

            Control newFleet = Named(window, "NewFleetButton");
            Control filter = Named(window, "CharacterFilter");
            Point newFleetAt = newFleet.TranslatePoint(default, window) ?? throw new InvalidOperationException("not placed");
            Point filterAt = filter.TranslatePoint(default, window) ?? throw new InvalidOperationException("not placed");

            Assert.True(filter.IsEffectivelyVisible);
            Assert.InRange(Math.Abs(newFleetAt.Y + newFleet.Bounds.Height / 2 - (filterAt.Y + filter.Bounds.Height / 2)), 0, 2);
            Assert.Equal(pilots + 1, vm.CharacterOptions.Count);
            Assert.Equal($"All characters ({pilots})", vm.CharacterOptions[0].Name);

            window.Close();
            vm.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task CharacterFilter_PickingAPilot_KeepsOnlyTheirFleets_AndAllBringsTheRestBack()
    {
        var (instance, vm) = await SceneAsync(6);
        using (instance)
        {
            Assert.Equal(2, vm.StandingByFleets.Count);

            vm.SelectedCharacterOption = vm.CharacterOptions.Single(option => option.CharacterId == FirstPilot + 5);
            Assert.Equal("Solo", Assert.Single(vm.StandingByFleets).Name);

            vm.SelectedCharacterOption = vm.CharacterOptions[0];
            Assert.Equal(2, vm.StandingByFleets.Count);
            vm.Dispose();
        }
    }

    private static Window Show(FleetsViewModel vm, double width)
    {
        var window = new FleetsWindow(vm) { Width = width, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        return window;
    }

    private static Control Named(Window window, string name) =>
        window.GetVisualDescendants().OfType<Control>().First(control => control.Name == name);

    /// <summary>Half the pilots stand by in "Pairs", so half the lanes carry the foot line and half do not; the last
    /// pilot alone stands by in "Solo".</summary>
    private static async Task<(TestClientInstance Instance, FleetsViewModel Vm)> SceneAsync(int pilots)
    {
        int[] ids = [.. Enumerable.Range(FirstPilot, pilots)];
        var transport = new RecordingFleetTransportClient();
        transport.MyFleetsByServer[Server] =
        [
            new FleetInfo(31, "Pairs", null, FleetVisibility.InviteOnly, FleetState.Active, ids[0], null, null,
                DateTimeOffset.UnixEpoch, FleetActivation.Forming),
            new FleetInfo(32, "Solo", null, FleetVisibility.InviteOnly, FleetState.Active, ids[^1], null, null,
                DateTimeOffset.UnixEpoch, FleetActivation.Forming),
        ];
        transport.MembersByFleet[31] = [.. ids.Take(pilots / 2).Select((id, i) =>
            new FleetMemberInfo(i + 1, id, 0, 0, i == 0 ? FleetRole.FleetCommander : FleetRole.SquadMember, false))];
        transport.MembersByFleet[32] = [new FleetMemberInfo(100, ids[^1], -1, -1, FleetRole.FleetCommander, false)];

        var instance = TestClientInstance.Create(s =>
        {
            s.AddSingleton<IFleetTransportClient>(transport);
            s.AddSingleton<IDialogService>(new RecordingDialogService());
            s.AddSingleton<ILocalCharacterPresence>(new NoPresence());
        });

        var registry = instance.Services.GetRequiredService<ICharacterRegistry>();
        foreach (int id in ids)
            await registry.AddOrUpdateAsync(new Character($"Pilot {id - FirstPilot + 1:00}", id));
        await instance.Services.GetRequiredService<IClientSessionStore>()
            .SaveAsync(Server, new ClientSessionTokens("t", "r", "Pilot 01", ids[0]));

        var vm = new FleetsViewModel(instance.Services, runClock: false);
        for (var i = 0; i < 100 && (vm.StandingByFleets.Count < 2 || vm.Lanes.Count < pilots); i++)
            await Task.Delay(50);
        return (instance, vm);
    }

    private sealed class NoPresence : ILocalCharacterPresence
    {
        public bool? IsInGame(int characterId, string? characterName) => null;
        public bool? IsInGame(int characterId) => null;
        public IDisposable Subscribe(Action handler) => new Nothing();
        private sealed class Nothing : IDisposable { public void Dispose() { } }
    }
}
