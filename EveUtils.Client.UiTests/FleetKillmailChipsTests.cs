using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Fleet;
using EveUtils.Client.Transport;
using EveUtils.Client.ViewModels;
using EveUtils.Client.Views;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Killmails.Entities;
using EveUtils.Shared.Modules.Killmails.Enums;
using EveUtils.Shared.Modules.Killmails.Events;
using EveUtils.Shared.Modules.Killmails.Repositories;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Transport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using UiDispatcher = Avalonia.Threading.Dispatcher;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-372: the LOST and KILLS chips on a fleet's member rows and the "LOSSES n · KILLS m" line on the active fleet row,
/// read from the local killmail store for the fleet's active period only.
/// </summary>
public sealed class FleetKillmailChipsTests
{
    private const string Server = "krahwinkel-it.nl:7443";
    private const long FleetId = 7;
    private const int Own = 95372001;
    private const int MateOne = 95372002;
    private const int MateTwo = 95372003;
    private const int MateThree = 95372004;
    private const int Enemy = 95379999;
    private const int Gila = 17715;
    private const int Rifter = 587;
    private const int Capsule = 670;

    private static readonly DateTime ActivatedUtc = DateTime.UtcNow.AddHours(-2);

    private static string? ShotsDirectory => Environment.GetEnvironmentVariable("ET372_SHOTS");

    [AvaloniaFact]
    public async Task ActiveFleet_ShowsLostAndKillsPerMember_AndFleetTotalsCountedOncePerMail()
    {
        (TestClientInstance instance, FleetsViewModel vm, RecordingDialogService dialogs) = await _BuildAsync();
        using (instance)
        {
            ILocalKillmailRepository store = instance.Services.GetRequiredService<ILocalKillmailRepository>();
            // Own pilot: a Gila, then the capsule behind it; and a kill on a mate's Rifter. A mail with a fleet
            // member as victim stays a loss even though a fleet mate attacked on it.
            await store.AddMissingAsync(Own, [_Mail(Own, 1, Gila, 10), _Mail(Own, 2, Capsule, 11), _Mail(Own, 3, Rifter, 30, isLoss: false)]);
            await store.AddMissingAsync(MateThree, [_Mail(MateThree, 3, Rifter, 30, shared: true)]);
            // One kill, three fleet members as attackers: KILLS 1 on each of them, and 1 on the fleet row.
            foreach (int pilot in new[] { MateOne, MateTwo, MateThree })
            {
                await store.AddMissingAsync(pilot, [_Mail(pilot, 40, Gila, 20, isLoss: false, shared: true)]);
            }

            await _PublishAndSettleAsync(instance, vm, Own);

            FleetViewModel fleet = vm.ActiveFleets.Single();
            Assert.Equal("LOSSES 3 · KILLS 1", fleet.KillmailTotalsText);
            FleetMemberRowViewModel own = _Member(fleet, Own);
            Assert.Equal("LOST Gila", own.LostText);
            Assert.Equal(1, own.Kills);
            Assert.False(_Member(fleet, MateOne).HasLost);
            Assert.Equal(1, _Member(fleet, MateOne).Kills);
            Assert.Equal("LOST Rifter", _Member(fleet, MateThree).LostText);
            Assert.Equal(1, _Member(fleet, MateThree).Kills);
            Assert.False(_Member(fleet, MateTwo).HasLost);

            own.OpenLostCommand?.Execute(null);
            Assert.Equal($"killmail-{Own}-1", dialogs.LastKillmailDetail?.ModuleId);

            _Render(vm, "fleet-overview");
            vm.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task ReceivedShare_UpdatesChipsAndTotals_OnlyForValidMails_WithoutAnotherRosterLoad()
    {
        (TestClientInstance instance, FleetsViewModel vm, RecordingDialogService _) = await _BuildAsync();
        using (instance)
        {
            ILocalKillmailRepository store = instance.Services.GetRequiredService<ILocalKillmailRepository>();
            // Not countable: before the fleet went active, and shared in another fleet.
            await store.AddMissingAsync(Own, [_Mail(Own, 50, Gila, -30, isLoss: false)]);
            await store.AddMissingAsync(MateOne, [_Mail(MateOne, 51, Gila, 15, isLoss: false, shared: true, fleetId: 99)]);
            await _PublishAndSettleAsync(instance, vm, Own);
            FleetViewModel fleet = vm.ActiveFleets.Single();
            FleetMemberRowViewModel mate = _Member(fleet, MateOne);
            Assert.Null(fleet.KillmailTotalsText);
            Assert.Equal(0, mate.Kills);

            // The roster would come back empty on a reload; the rows below must be the same instances afterwards.
            ((RecordingFleetTransportClient)instance.Services.GetRequiredService<IFleetTransportClient>()).MembersByFleet[FleetId] = [];
            await store.AddMissingAsync(MateOne, [_Mail(MateOne, 52, Gila, 16, isLoss: false, shared: true)]);
            await _PublishAndSettleAsync(instance, vm, MateOne);

            Assert.Equal("LOSSES 0 · KILLS 1", fleet.KillmailTotalsText);
            Assert.Same(mate, _Member(vm.ActiveFleets.Single(), MateOne));
            Assert.Equal(1, mate.Kills);
            vm.Dispose();
        }
    }

    private static FleetMemberRowViewModel _Member(FleetViewModel fleet, int characterId) =>
        fleet.Members.Single(member => member.CharacterId == characterId);

    private static LocalKillmail _Mail(int characterId, int killmailId, int shipTypeId, int minutesAfterActive,
        bool isLoss = true, bool shared = false, long fleetId = FleetId) => new()
    {
        CharacterId = characterId,
        KillmailId = killmailId,
        Hash = new string('a', 40),
        KillmailTimeUtc = ActivatedUtc.AddMinutes(minutesAfterActive),
        SolarSystemId = 30000142,
        IsLoss = isLoss,
        VictimShipTypeId = shipTypeId,
        VictimCharacterId = isLoss ? characterId : Enemy,
        ImportedAtUtc = DateTime.UtcNow,
        SharedFromFleetId = shared ? fleetId : null,
    };

    private static async Task _PublishAndSettleAsync(TestClientInstance instance, FleetsViewModel vm, int characterId)
    {
        await instance.Services.GetRequiredService<IEventBus>().PublishAsync(
            new KillmailsChangedEvent(characterId, KillmailsChangeKind.FleetShareChanged), EventTarget.Local);
        for (int i = 0; i < 160; i++)
        {
            UiDispatcher.UIThread.RunJobs();
            await Task.Delay(25);
        }
    }

    private static void _Render(FleetsViewModel vm, string name)
    {
        if (ShotsDirectory is not { } directory)
        {
            return;
        }

        vm.ActiveFleets.Single().ToggleExpandedCommand.Execute(null);
        var window = new FleetsWindow(vm) { Width = 1578, Height = 700 };
        window.Show();
        Directory.CreateDirectory(directory);
        foreach ((int width, string suffix) in new[] { (1578, "wide"), (758, "narrow") })
        {
            window.Width = width;
            for (int i = 0; i < 16; i++)
            {
                UiDispatcher.UIThread.RunJobs();
            }

            window.UpdateLayout();
            window.CaptureRenderedFrame()?.Save(Path.Combine(directory, $"{name}-{suffix}.png"), new PngBitmapEncoderOptions());
        }

        window.Close();
    }

    private static async Task<(TestClientInstance Instance, FleetsViewModel Vm, RecordingDialogService Dialogs)> _BuildAsync()
    {
        var transport = new RecordingFleetTransportClient();
        var dialogs = new RecordingDialogService();
        FleetInfo fleet = new(FleetId, "Sansha evening Otanuomi", null, FleetVisibility.Public, FleetState.Active, Own, null, null,
            DateTimeOffset.UtcNow.AddDays(-1), FleetActivation.Active, ActivatedAt: new DateTimeOffset(ActivatedUtc, TimeSpan.Zero));
        transport.MyFleetsByServer[Server] = [fleet];
        transport.OpenFleetsByServer[Server] = [];
        transport.MembersByFleet[FleetId] =
        [
            _Roster(1, Own, fc: true), _Roster(2, MateOne), _Roster(3, MateTwo), _Roster(4, MateThree),
        ];
        var lookup = new FakeExternalLookup { [MateOne] = "Tessa Korrin", [MateTwo] = "Doro Vanth", [MateThree] = "Vaari Onc" };

        TestClientInstance instance = TestClientInstance.Create(services =>
        {
            services.AddSingleton<IFleetTransportClient>(transport);
            services.AddSingleton<IDialogService>(dialogs);
            services.AddSingleton<IExternalCharacterLookup>(lookup);
            services.AddSingleton<ISdeAccessor>(new FakeSdeAccessor().Add(Gila, "Gila", 26, 6).Add(Rifter, "Rifter", 25, 6));
        });
        await instance.Services.GetRequiredService<ICharacterRegistry>().AddOrUpdateAsync(new Character("Ravnholt", Own));
        await instance.Services.GetRequiredService<IClientSessionStore>().SaveAsync(Server, new ClientSessionTokens("t", "r", "Ravnholt", Own));

        var vm = new FleetsViewModel(instance.Services, runClock: false);
        for (int i = 0; i < 200 && vm.ActiveFleets.Count == 0; i++)
        {
            UiDispatcher.UIThread.RunJobs();
            await Task.Delay(25);
        }

        return (instance, vm, dialogs);
    }

    private static FleetMemberInfo _Roster(long id, int characterId, bool fc = false) =>
        new(id, characterId, fc ? -1 : 0, 0, fc ? FleetRole.FleetCommander : FleetRole.SquadMember, false, null, null, default,
            DateTimeOffset.UtcNow);
}
