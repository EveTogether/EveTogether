using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using EveUtils.Client.Fleet;
using EveUtils.Client.Gamelog;
using EveUtils.Client.Runs;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Gamelog.Models;
using EveUtils.Shared.Modules.Gamelog.Parsing;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Events;
using EveUtils.Shared.Modules.Sde.Dtos;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// <see cref="HomefrontDetector"/> (ET-348): a homefront's hallmark NPC in the game log, seen by an own character in
/// a fleet, offers that site's run once — and the one click on Start opens it.
/// </summary>
public sealed class HomefrontDetectorTests
{
    private const int Pilot = 90000001;
    private const long FleetId = 42;
    private static readonly DateTime SeenAtUtc = new(2026, 9, 24, 10, 0, 2, DateTimeKind.Utc);

    [AvaloniaFact]
    public async Task AnOffertorySigilSeenTwice_InAFleet_OffersTheRaidOnce_AndStartOpensItsRun()
    {
        using var env = await Env.StartAsync();

        await env.HitAsync("Offertory Sigil", SeenAtUtc);
        await env.HitAsync("Offertory Sigil", SeenAtUtc.AddSeconds(5));
        await env.HitAsync("Offertory Sigil", SeenAtUtc.AddSeconds(9));
        await ActivityWindowHarness.WaitUntil(() => env.Toasts.ActionToasts.Count > 0);

        var offer = Assert.Single(env.Toasts.ActionToasts);
        Assert.Equal("Start Homefront run: Raid: Hall of Sacrifice?", offer.Title);
        Assert.Equal(["Ignore", "Start"], offer.Actions.Select(action => action.Label));

        offer.Actions[1].Run();
        await ActivityWindowHarness.WaitUntil(() => env.Dialogs.ShownActivityWindows.Count > 0);

        ActivityWindowViewModel window = Assert.Single(env.Dialogs.ShownActivityWindows);
        Assert.Equal((Pilot, "Jithran"), window.PickedCharacter);
        Assert.Equal(10347, Assert.Single(window.MatchedSites).DungeonId);
        Assert.True(window.StartsOnArrival);
    }

    [AvaloniaFact]
    public async Task ADestabilizingArrayNeutedTwice_OffersStabilizeRift()
    {
        using var env = await Env.StartAsync();

        env.Neut(SeenAtUtc);
        env.Neut(SeenAtUtc.AddSeconds(6));
        await ActivityWindowHarness.WaitUntil(() => env.Toasts.ActionToasts.Count > 0);

        Assert.Equal("Start Homefront run: Stabilize Rift?", Assert.Single(env.Toasts.ActionToasts).Title);
    }

    // One scenario rather than three tests: each rule only means something against the state the one before it left.
    // The Badger Runner at the end is a sentinel, so a stray Raid offer from any earlier step has landed by then.
    [AvaloniaFact]
    public async Task ARunningRun_ARunJustDiscarded_OrAnIgnoredOffer_NeverGetsASecondOffer()
    {
        using var env = await Env.StartAsync();
        var runId = Guid.NewGuid();

        await env.Bus.PublishAsync(new RunStartedEvent(runId, Pilot, ActivityKind.Site, SeenAtUtc, FleetId, "HF-TEST",
            isFleetCommander: true, siteName: "Raid: Hall of Sacrifice"));
        await env.HitAsync("Offertory Sigil", SeenAtUtc);
        await env.HitAsync("Offertory Sigil", SeenAtUtc.AddSeconds(5));

        await env.Bus.PublishAsync(new RunDeletedEvent(runId));
        await env.HitAsync("Offertory Sigil", SeenAtUtc.AddSeconds(20));
        await env.HitAsync("Offertory Sigil", SeenAtUtc.AddSeconds(25));

        env.Clock.Now += TimeSpan.FromMinutes(3);
        await env.HitAsync("Offertory Sigil", SeenAtUtc.AddMinutes(3));
        await env.HitAsync("Offertory Sigil", SeenAtUtc.AddMinutes(3).AddSeconds(5));
        await ActivityWindowHarness.WaitUntil(() => env.Toasts.ActionToasts.Count > 0);
        env.Toasts.ActionToasts[0].Actions[0].Run();
        await env.HitAsync("Offertory Sigil", SeenAtUtc.AddMinutes(3).AddSeconds(10));
        await env.HitAsync("Offertory Sigil", SeenAtUtc.AddMinutes(3).AddSeconds(15));

        await env.HitAsync("Badger Runner", SeenAtUtc.AddMinutes(4));
        await env.HitAsync("Badger Runner", SeenAtUtc.AddMinutes(4).AddSeconds(5));
        await ActivityWindowHarness.WaitUntil(() => env.Toasts.ActionToasts.Count > 1);

        Assert.Equal(["Start Homefront run: Raid: Hall of Sacrifice?", "Start Homefront run: Raid: Black Market?"],
            env.Toasts.ActionToasts.Select(toast => toast.Title));
    }

    private sealed class Env : IDisposable
    {
        private readonly TestClientInstance _instance;
        private readonly HomefrontDetector _detector;
        private readonly GamelogClientService _gamelog;

        public RecordingToastService Toasts { get; } = new();

        public RecordingDialogService Dialogs { get; } = new();

        public MutableTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);

        public IEventBus Bus => _instance.Services.GetRequiredService<IEventBus>();

        private Env()
        {
            FakeSdeAccessor sde = new FakeSdeAccessor()
                .Add(77066, "Offertory Sigil", groupId: 4577, categoryId: 11)
                .Add(77505, "Badger Runner", groupId: 4577, categoryId: 11)
                .Add(81402, "Destabilizing Array", groupId: 4576, categoryId: 11)
                .AddSite(_Homefront(10347, "Raid: Hall of Sacrifice"))
                .AddSite(_Homefront(10377, "Raid: Black Market"))
                .AddSite(_Homefront(10714, "Stabilize Rift"));
            _instance = TestClientInstance.Create(services => services.AddSingleton<TimeProvider>(Clock));
            _gamelog = _instance.Services.GetRequiredService<GamelogClientService>();
            _detector = new HomefrontDetector(_gamelog, Bus, sde, Toasts, Dialogs, _instance.Services);
        }

        public static async Task<Env> StartAsync()
        {
            var env = new Env();
            await env._instance.Services.GetRequiredService<ICharacterRegistry>()
                .AddOrUpdateAsync(new Character("Jithran", Pilot));
            env._gamelog.MapCharacter(Pilot, "Jithran");
            env._instance.Services.GetRequiredService<IFleetParticipation>()
                .Set([new FleetParticipant(Pilot, FleetId, ClientOnly: true)]);
            return env;
        }

        public async Task HitAsync(string target, DateTime atUtc)
        {
            var hit = (CombatEvent)(LogLineParser.Parse(
                $"[ {atUtc:yyyy.MM.dd HH:mm:ss} ] (combat) <color=0xff00ffff><b>1192</b> <color=0x77ffffff><font size=10>to</font> <b><color=0xffffffff>{target}</b><font size=10><color=0x77ffffff> - Nova Rage Heavy Assault Missile - Hits")
                ?? throw new InvalidOperationException("The damage line did not parse."));
            await _gamelog.AddHitAsync("Jithran", hit.Direction, hit.Amount, hit.Target, hit.Quality, hit.Timestamp, hit.Weapon);
            Dispatcher.UIThread.RunJobs();
        }

        public void Neut(DateTime atUtc)
        {
            var neut = (NeutEvent)(LogLineParser.Parse(
                $"[ {atUtc:yyyy.MM.dd HH:mm:ss} ] (combat) <color=0xff7fffff><b>50 GJ</b><color=0x77ffffff><font size=10> energy neutralized </font><b><color=0xffffffff>Destabilizing Array</b><color=0x77ffffff><font size=10> - Small Energy Neutralizer II</font>")
                ?? throw new InvalidOperationException("The neut line did not parse."));
            _gamelog.AddNeut("Jithran", neut.Outgoing, neut.Amount, neut.Timestamp, neut.Source);
            Dispatcher.UIThread.RunJobs();
        }

        public void Dispose()
        {
            _detector.Dispose();
            _instance.Dispose();
        }

        private static SdeSite _Homefront(int dungeonId, string name) =>
            new(dungeonId, name, 70, "Homefront Operations", null, null, null, null, false, []);
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
