using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Esi;
using EveUtils.Client.Fleet;
using EveUtils.Client.Gamelog;
using EveUtils.Client.Runs;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Shared.Data;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-500: a pilot who comes into a fleet abyssal after its start gets into the running run with one click — another
/// pilot on their own client, off the commander's heartbeat, or a second own character on the same client, off the
/// window. Either can already be inside, on a client that never saw them outside: the leg then starts at the join.
/// </summary>
public sealed class FleetRunLateJoinTests
{
    private const string GroupCode = "AB-L4TE";
    private const int AbyssalRoom = 32000042;
    private const int Abnoba = 90000020;

    [AvaloniaFact]
    public async Task APilotWhoJoinsTheFleetLater_JoinsTheRunningRunInOneClick_AndTheCommanderSeesTheirLeg()
    {
        LocationWatch watch = new();
        Pilot jithran = await Pilot.CreateAsync(FleetOfTwo.JithranId, "Jithran", kind: ActivityKind.Abyssal);
        Pilot raymond = await Pilot.CreateAsync(FleetOfTwo.RaymondId, "Raymond",
            services => services.AddSingleton<IEsiLocationMonitor>(watch), ActivityKind.Abyssal);
        using (jithran)
        using (raymond)
        {
            FleetRunLegs legs = jithran.Instance.Services.GetRequiredService<FleetRunLegs>();
            await jithran.Window.RefreshFleetCommandAsync(DateTime.UtcNow);
            jithran.Window.JoinFleetRun(new RunGroupCodeStart(FleetOfTwo.FleetId, ActivityKind.Abyssal, GroupCode,
                DateTime.UtcNow.AddMinutes(-3), IsFleetCommander: true));
            await jithran.Window.StartRunCommand.ExecuteAsync(null);

            raymond.Window.Dispose();
            IEventBus raymondsBus = raymond.Instance.Services.GetRequiredService<IEventBus>();
            RecordingDialogService raymondsDialogs = (RecordingDialogService)raymond.Instance.Services
                .GetRequiredService<EveUtils.Client.Dialogs.IDialogService>();
            RecordingToastService raymondsToasts = (RecordingToastService)raymond.Instance.Services
                .GetRequiredService<EveUtils.Client.Notifications.IToastService>();
            using FleetRunWindowPresenter presenter = new(raymondsBus, raymondsDialogs, raymond.Instance.Services);
            raymond.Instance.Services.GetRequiredService<GamelogClientService>().MapCharacter(FleetOfTwo.RaymondId, "Raymond");
            watch.Report(FleetOfTwo.RaymondId, AbyssalRoom, DateTime.UtcNow);

            jithran.Wire.Destinations.Add(raymond.Instance.Services);
            raymond.Wire.Destinations.Add(jithran.Instance.Services);
            DateTime heartbeatAt = DateTime.UtcNow;
            jithran.Window.Refresh(heartbeatAt.AddSeconds(31));
            await _SettleAsync(() => raymondsToasts.ActionToasts.Count > 0);
            int heartbeatsSent = jithran.Wire.Sent.Count(sent => sent is FleetRunRunningEvent);
            jithran.Window.Refresh(heartbeatAt.AddSeconds(62));
            await _SettleAsync(() => false, attempts: 10);

            Assert.True(jithran.Wire.Sent.Count(sent => sent is FleetRunRunningEvent) > heartbeatsSent);
            var offer = Assert.Single(raymondsToasts.ActionToasts);
            Assert.Equal("Fleet run in progress", offer.Title);
            DateTime clickedAt = DateTime.UtcNow;
            offer.Actions.Single(action => action.Label == "Join running run").Run();
            await _SettleAsync(() => raymondsDialogs.ShownActivityWindows.Count > 0);

            using ActivityWindowViewModel window = Assert.Single(raymondsDialogs.ShownActivityWindows);
            await window.LoadAsync();
            await _SettleAsync(() => window.RunId is not null, refresh: window);

            Assert.Equal(ActivityRunState.Running, window.RunState);
            Assert.Equal(GroupCode, window.GroupCode);
            Assert.True(window.AnchorUtc >= clickedAt.AddSeconds(-1), "the leg starts at the join, not at the commander's start");
            await _SettleAsync(() => legs.Of(GroupCode).Any(leg => leg.CharacterId == FleetOfTwo.RaymondId));
            Assert.Contains(legs.Of(GroupCode), leg => leg.CharacterId == FleetOfTwo.RaymondId && leg.StoppedAtUtc is null);
            await FleetOfTwo.RunJobsAsync();
        }
    }

    [AvaloniaFact]
    public async Task ASecondOwnCharacterInThePocket_IsOfferedAndJoinsInOneClick_UnderTheSameGroupCode()
    {
        LocationWatch watch = new();
        Pilot jithran = await Pilot.CreateAsync(FleetOfTwo.JithranId, "Jithran",
            services => services.AddSingleton<IEsiLocationMonitor>(watch), ActivityKind.Abyssal);
        using (jithran)
        {
            jithran.Window.Dispose();
            IServiceProvider services = jithran.Instance.Services;
            FleetRunLegs legs = services.GetRequiredService<FleetRunLegs>();
            await services.GetRequiredService<ICharacterRegistry>().AddOrUpdateAsync(new Character("Abnoba Auscent", Abnoba));
            services.GetRequiredService<GamelogClientService>().MapCharacter(Abnoba, "Abnoba Auscent");

            using ActivityWindowViewModel window = new(ActivityKind.Abyssal, services);
            window.UseCharacter(FleetOfTwo.JithranId, "Jithran");
            await window.LoadAsync();
            await window.RefreshFleetCommandAsync(DateTime.UtcNow);
            window.JoinFleetRun(new RunGroupCodeStart(FleetOfTwo.FleetId, ActivityKind.Abyssal, GroupCode,
                DateTime.UtcNow.AddMinutes(-3), IsFleetCommander: true));
            await window.StartRunCommand.ExecuteAsync(null);
            Guid ownRunId = window.RunId ?? throw new InvalidOperationException("the commander's run did not start");
            window.Refresh(DateTime.UtcNow);
            Assert.False(window.IsLateJoinShown);

            watch.Report(Abnoba, AbyssalRoom, DateTime.UtcNow);
            window.Refresh(DateTime.UtcNow);

            Assert.True(window.IsLateJoinShown);
            Assert.Equal("Abnoba Auscent is in the pocket but not in this run.", window.LateJoinText);
            Assert.True(window.HasCompactNoticeContent);

            DateTime clickedAt = DateTime.UtcNow;
            await window.JoinRunningRunCommand.ExecuteAsync(null);
            await _SettleAsync(() => window.Participants.Any(participant => participant.CharacterId == Abnoba), refresh: window);

            window.Refresh(DateTime.UtcNow);
            Assert.False(window.IsLateJoinShown);
            await using ClientDbContext db = await services
                .GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync();
            Run abnobasRun = await db.Set<Run>().AsNoTracking().SingleAsync(run => run.CharacterId == Abnoba);
            Assert.NotEqual(ownRunId, abnobasRun.Id);
            Assert.Equal(GroupCode, abnobasRun.GroupCode);
            Assert.Equal(EveUtils.Shared.Modules.Runs.Enums.RunState.Running, abnobasRun.State);
            Assert.True(abnobasRun.StartedAtUtc >= clickedAt.AddSeconds(-1));
            Assert.Contains(legs.Of(GroupCode), leg => leg.CharacterId == Abnoba);
            Assert.Equal(ActivityRunState.Running, window.RunState);
            await FleetOfTwo.RunJobsAsync();
        }
    }

    [AvaloniaFact]
    public async Task AnOwnCharacterOutsideThePocket_IsNotOffered()
    {
        LocationWatch watch = new();
        Pilot jithran = await Pilot.CreateAsync(FleetOfTwo.JithranId, "Jithran",
            services => services.AddSingleton<IEsiLocationMonitor>(watch), ActivityKind.Abyssal);
        using (jithran)
        {
            jithran.Window.Dispose();
            IServiceProvider services = jithran.Instance.Services;
            await services.GetRequiredService<ICharacterRegistry>().AddOrUpdateAsync(new Character("Abnoba Auscent", Abnoba));
            services.GetRequiredService<GamelogClientService>().MapCharacter(Abnoba, "Abnoba Auscent");

            using ActivityWindowViewModel window = new(ActivityKind.Abyssal, services);
            window.UseCharacter(FleetOfTwo.JithranId, "Jithran");
            await window.LoadAsync();
            await window.StartRunCommand.ExecuteAsync(null);

            watch.Report(Abnoba, 30002718, DateTime.UtcNow);
            window.Refresh(DateTime.UtcNow);

            Assert.False(window.IsLateJoinShown);
            await FleetOfTwo.RunJobsAsync();
        }
    }

    private static async Task _SettleAsync(Func<bool> until, ActivityWindowViewModel? refresh = null, int attempts = 100)
    {
        for (int attempt = 0; attempt < attempts && !until(); attempt++)
        {
            refresh?.Refresh(DateTime.UtcNow);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
    }

    /// <summary>The ESI location poll, per character, so a test can put one pilot in a pocket the client never saw
    /// them enter.</summary>
    private sealed class LocationWatch : IEsiLocationMonitor
    {
        private readonly Dictionary<int, Action<EsiLocationReading>> _readers = [];

        public void Watch(int characterId, string characterName, Action<EsiLocationReading> onReading) =>
            _readers[characterId] = onReading;

        public void UiReady() { }

        public void Stop(int characterId) => _readers.Remove(characterId);

        public bool IsWatching(int characterId) => _readers.ContainsKey(characterId);

        public void Report(int characterId, int solarSystemId, DateTime atUtc)
        {
            if (_readers.TryGetValue(characterId, out Action<EsiLocationReading>? onReading))
                onReading(new EsiLocationReading(solarSystemId, atUtc));
        }
    }
}
