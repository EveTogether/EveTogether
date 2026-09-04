// Throwaway render harness for ET-168 — screenshots of the start dialog with and without a collision, the switch
// dialog, the inbox row that carries a switch request, and the overview's member row and lane with the switch on
// them. Not a test that guards anything; removed before the PR is merged.
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Fleet;
using EveUtils.Client.Platform;
using EveUtils.Client.Theming;
using EveUtils.Client.Transport;
using EveUtils.Client.ViewModels;
using EveUtils.Client.Views;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Repositories;
using EveUtils.Shared.Modules.Messaging.Entities;
using EveUtils.Shared.Modules.Messaging.Repositories;
using EveUtils.Shared.Transport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

public class Et168RenderHarness
{
    private const string Server = "krahwinkel-it.nl:7443";
    private const int Ravnholt = 1001, Kaska = 1002, Torv = 1003, Bex = 1004;
    private const int Aurel = 900001, Tessa = 900002, Doro = 900003, Vaari = 900004, Selo = 900005;

    private static readonly string OutDir =
        Environment.GetEnvironmentVariable("ET168_SHOTS") ?? @"C:\Users\info\Mockups\et168-renders\ronde-1";

    private sealed class FakePresence(HashSet<int> mine, HashSet<int> online) : ILocalCharacterPresence
    {
        public bool? IsInGame(int characterId, string? characterName) => mine.Contains(characterId) ? online.Contains(characterId) : null;
        public bool? IsInGame(int characterId) => IsInGame(characterId, null);
        public IDisposable Subscribe(Action handler) => new Nothing();
        private sealed class Nothing : IDisposable { public void Dispose() { } }
    }

    private static FleetInfo Fleet(long id, string name, int owner, FleetActivation activation, FleetVisibility visibility,
        DateTimeOffset? activatedAt, DateTimeOffset created) =>
        new(id, name, null, visibility, FleetState.Active, owner, null, null, created, activation, ActivatedAt: activatedAt);

    private static FleetMemberInfo Member(long id, int characterId, bool fc = false, bool external = false, DateTimeOffset? lastSeen = null) =>
        new(id, characterId, fc ? -1 : 0, 0, fc ? FleetRole.FleetCommander : FleetRole.SquadMember, external, null, null, default, lastSeen);

    // ── Screen 2: the start dialog ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Scherm 2's roster: six on the roster, five with a client, three mine — one of the colliding pair is
    /// my own alt (Bex) and the other is someone else's pilot (Tessa), because the dialog says different things
    /// about the two.</summary>
    private static FleetStartPrompt CollisionPrompt() => new(
        "Sunday DED run",
        [
            new FleetStartMember(Ravnholt, "Ravnholt", IsMine: true, IsCommander: true, IsExternal: false, null),
            new FleetStartMember(Kaska, "Kaska Vex", IsMine: true, IsCommander: false, IsExternal: false, null),
            new FleetStartMember(Bex, "Bex Harrow", IsMine: true, IsCommander: false, IsExternal: false, "Sansha evening Otanuomi"),
            new FleetStartMember(Tessa, "Tessa Korrin", IsMine: false, IsCommander: false, IsExternal: false, "Wormhole night"),
            new FleetStartMember(Doro, "Doro Vanth", IsMine: false, IsCommander: false, IsExternal: false, null),
            new FleetStartMember(Vaari, "Vaari Onc", IsMine: false, IsCommander: false, IsExternal: true, null),
        ],
        CanAskThemAll: true);

    private static FleetStartPrompt CleanPrompt() => new(
        "Wednesday Homefronts",
        [
            new FleetStartMember(Ravnholt, "Ravnholt", IsMine: true, IsCommander: true, IsExternal: false, null),
            new FleetStartMember(Kaska, "Kaska Vex", IsMine: true, IsCommander: false, IsExternal: false, null),
            new FleetStartMember(Torv, "Torv Kesh", IsMine: true, IsCommander: false, IsExternal: false, null),
            new FleetStartMember(Selo, "Selo Kaine", IsMine: false, IsCommander: false, IsExternal: true, null),
        ],
        CanAskThemAll: false);

    private static SwitchFleetPrompt SwitchPrompt(DateTimeOffset now) => new(
        "Bex Harrow",
        "Sunday DED run",
        now.AddMinutes(-3),
        [new SwitchFleetLeaving("Sansha evening Otanuomi", now.AddHours(-2).AddMinutes(-4))],
        ["Bex Harrow — Fortress Sansha, 00:11:42"]);

    // ── Screen 1 / 7: the overview and the inbox, on the ET-170 scene ───────────────────────────────────────────

    private static async Task<(TestClientInstance Instance, FleetsViewModel Vm, DateTimeOffset Now)> BuildOverviewAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var ownNames = new Dictionary<int, string>
        {
            [Ravnholt] = "Ravnholt", [Kaska] = "Kaska Vex", [Torv] = "Torv Kesh", [Bex] = "Bex Harrow",
        };
        var mine = ownNames.Keys.ToHashSet();
        var online = new HashSet<int> { Ravnholt, Kaska, Torv, Bex };

        var lookup = new FakeExternalLookup
        {
            [Aurel] = "Aurel Vantis", [Tessa] = "Tessa Korrin", [Doro] = "Doro Vanth", [Vaari] = "Vaari Onc", [Selo] = "Selo Kaine",
        };

        var transport = new RecordingFleetTransportClient();
        // Sansha started first, so Bex counts there and is "elsewhere active" on the local Wednesday fleet below —
        // exactly the collision scherm 1 and 2 draw.
        var fleets = new List<FleetInfo>
        {
            Fleet(21, "Sansha evening Otanuomi", Aurel, FleetActivation.Active, FleetVisibility.Public, now.AddHours(-2).AddMinutes(-4).AddSeconds(-8), now.AddDays(-3)),
            Fleet(22, "Sunday DED run", Ravnholt, FleetActivation.Active, FleetVisibility.InviteOnly, now.AddMinutes(-3), now.AddDays(-20)),
        };
        transport.MembersByFleet[21] = [Member(1, Aurel, fc: true, lastSeen: now), Member(2, Bex, lastSeen: now), Member(3, Tessa, lastSeen: now)];
        transport.MembersByFleet[22] =
        [
            Member(5, Ravnholt, fc: true, lastSeen: now), Member(6, Kaska, lastSeen: now), Member(7, Torv, lastSeen: now),
            Member(8, Bex, lastSeen: now), Member(9, Tessa, lastSeen: now), Member(10, Vaari, external: true),
        ];
        transport.MyFleetsByServer[Server] = fleets;
        transport.OpenFleetsByServer[Server] = [];

        var instance = TestClientInstance.Create(s =>
        {
            s.AddSingleton<IFleetTransportClient>(transport);
            s.AddSingleton<IDialogService>(new RecordingDialogService());
            s.AddSingleton<IExternalCharacterLookup>(lookup);
            s.AddSingleton<ILocalCharacterPresence>(new FakePresence(mine, online));
        });
        instance.Services.GetRequiredService<IThemeService>().Apply(FactionTheme.Gallente);

        var registry = instance.Services.GetRequiredService<ICharacterRegistry>();
        foreach (var (id, name) in ownNames)
            await registry.AddOrUpdateAsync(new Character(name, id));
        var sessions = instance.Services.GetRequiredService<IClientSessionStore>();
        foreach (int id in new[] { Ravnholt, Kaska, Torv, Bex })
            await sessions.SaveAsync(Server, new ClientSessionTokens("t", "r", ownNames[id], id));

        var local = instance.Services.GetRequiredService<ClientFleetService>();
        var repository = instance.Services.GetRequiredService<IFleetRepository>();
        var wed = (await local.CreateLocalFleetAsync("Wednesday Homefronts", null, Ravnholt)).Value;
        foreach (int id in new[] { Kaska, Torv })
            await local.AddLocalCharacterAsync(wed, id, Ravnholt);
        await local.AddExternalAsync(wed, Selo, Ravnholt);

        var vm = new FleetsViewModel(instance.Services, runClock: false);
        for (var i = 0; i < 200 && !(vm.LocalFleets.Count == 1 && vm.ServerGroups.Count == 1 && vm.ActiveFleets.Count >= 2); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(25);
        }
        Dispatcher.UIThread.RunJobs();
        vm.Tick(now);
        return (instance, vm, now);
    }

    private static async Task<(TestClientInstance Instance, InboxViewModel Vm)> BuildInboxAsync()
    {
        var instance = TestClientInstance.Create(s =>
        {
            s.AddSingleton<IFleetTransportClient>(new RecordingFleetTransportClient());
            s.AddSingleton<IDialogService>(new RecordingDialogService());
        });
        instance.Services.GetRequiredService<IThemeService>().Apply(FactionTheme.Gallente);

        await instance.Services.GetRequiredService<ICharacterRegistry>()
            .AddOrUpdateAsync(new Character("Tessa Korrin", Tessa));

        var store = instance.Services.GetRequiredService<IClientInboxStore>();
        await store.UpsertAsync(new ClientInboxMessage
        {
            ServerMessageId = 5001,
            RecipientCharacterId = Tessa,
            ServerAddress = Server,
            SenderCharacterId = Ravnholt,
            Kind = MessageKind.FleetSwitchRequest,
            RefId = 22,
            Title = "We have started — are you coming? Sunday DED run",
            Body = "You are on Sunday DED run's roster, but you are sharing with 'Wormhole night'. While that is so "
                 + "you do not count here. Switching leaves Wormhole night and links you to Sunday DED run; staying "
                 + "where you are keeps you on this roster, just not linked.",
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            ReceivedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            Status = MessageStatus.Pending,
        });
        await store.UpsertAsync(new ClientInboxMessage
        {
            ServerMessageId = 5002,
            RecipientCharacterId = Tessa,
            ServerAddress = Server,
            SenderCharacterId = Aurel,
            Kind = MessageKind.FleetInvite,
            RefId = 77,
            Title = "Fleet invite: Wormhole night",
            Body = "Aurel Vantis invited you to Wormhole night.",
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-30),
            ReceivedAt = DateTimeOffset.UtcNow.AddMinutes(-30),
            Status = MessageStatus.Pending,
        });

        var vm = instance.Services.GetRequiredService<InboxViewModel>();
        await vm.OnOpenedAsync();
        Dispatcher.UIThread.RunJobs();
        return (instance, vm);
    }

    // ── Plumbing ───────────────────────────────────────────────────────────────────────────────────────────────

    private static void Settle(int times = 12)
    {
        for (int i = 0; i < times; i++)
            Dispatcher.UIThread.RunJobs();
    }

    private static void Shot(Window window, string name)
    {
        Settle();
        window.UpdateLayout();
        Settle(4);
        Directory.CreateDirectory(OutDir);
        window.CaptureRenderedFrame()!.Save(Path.Combine(OutDir, name + ".png"), new PngBitmapEncoderOptions());
    }

    /// <summary>Every text the dialog put on screen, with the x it landed on — so the comparison with the mockup is
    /// read off measurements rather than guessed from a screenshot.</summary>
    private static void LogDialog(StringBuilder log, string title, Window window)
    {
        var root = (Control)window.Content!;
        log.AppendLine($"== {title}  window w={window.Width:0.#} h={window.Bounds.Height:0.#} root h={root.Bounds.Height:0.#}");
        foreach (var text in root.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsVisible && !string.IsNullOrWhiteSpace(t.Text)))
        {
            var point = ((Visual)text).TranslatePoint(new Point(0, 0), root);
            log.AppendLine($"  text [{string.Join(" ", text.Classes)}] x={point?.X:0.#} y={point?.Y:0.#} w={text.Bounds.Width:0.#} fs={text.FontSize:0.#} :: {text.Text}");
        }
        foreach (var button in root.GetVisualDescendants().OfType<Button>().Where(b => b.IsVisible))
        {
            var point = ((Visual)button).TranslatePoint(new Point(0, 0), root);
            log.AppendLine($"  button [{string.Join(" ", button.Classes)}] x={point?.X:0.#} y={point?.Y:0.#} w={button.Bounds.Width:0.#} enabled={button.IsEnabled} :: {button.Content}");
        }
        foreach (var chip in root.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("chip") && b.IsVisible))
        {
            var point = ((Visual)chip).TranslatePoint(new Point(0, 0), root);
            var text = chip.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault()?.Text;
            log.AppendLine($"  chip [{string.Join(" ", chip.Classes)}] x={point?.X:0.#} y={point?.Y:0.#} w={chip.Bounds.Width:0.#} h={chip.Bounds.Height:0.#} :: {text}");
        }
    }

    private static void LogOverview(StringBuilder log, string title, FleetsViewModel vm)
    {
        log.AppendLine($"== {title}");
        foreach (var lane in vm.Lanes)
            log.AppendLine($"  lane {lane.CharacterName}: {lane.RoleChipText} | {lane.FleetText} | {lane.PrimaryActionText}(warn={lane.PrimaryIsWarn})/{lane.SecondaryActionText} | menu={string.Join(", ", lane.MenuItems.Select(m => m.Label))}");
        foreach (var row in vm.ActiveFleets.Concat(vm.StandingByFleets))
        {
            log.AppendLine($"  row {row.Name} expanded={row.IsExpanded}");
            foreach (var member in row.VisibleMembers)
                log.AppendLine($"    member {member.CharacterName}: link={member.LinkText} mine={member.IsMine} canSwitch={member.CanSwitch} label={member.SwitchLabel} note={member.ElsewhereNote}");
        }
    }

    [AvaloniaFact]
    public async Task Render_All()
    {
        var log = new StringBuilder();
        var now = DateTimeOffset.UtcNow;

        // The faction resources the dialogs read (WarnBrush and the rest) are applied by the theme service onto the
        // application, so one instance has to stand before any window is built — a dialog constructed before it
        // throws on the very first StaticResource.
        using var app = TestClientInstance.Create(s =>
        {
            s.AddSingleton<IFleetTransportClient>(new RecordingFleetTransportClient());
            s.AddSingleton<IDialogService>(new RecordingDialogService());
        });
        app.Services.GetRequiredService<IThemeService>().Apply(FactionTheme.Gallente);

        // Scherm 2 — the start dialog, with the collision and without it.
        var collide = new StartFleetWindow(CollisionPrompt());
        collide.Show();
        Shot(collide, "01-start-collision");
        LogDialog(log, "01 start with a collision", collide);
        // The same dialog after pressing "ask them all", so the footer chip and the pressed pick are both seen.
        collide.GetVisualDescendants().OfType<Button>().First(b => (b.Content as string) == "ask them all to switch")
            .Command?.Execute(null);
        foreach (var button in collide.GetVisualDescendants().OfType<Button>().Where(b => (b.Content as string) == "ask them all to switch"))
            button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Shot(collide, "02-start-collision-asking");
        LogDialog(log, "02 start with ask them all pressed", collide);
        collide.Close();

        var clean = new StartFleetWindow(CleanPrompt());
        clean.Show();
        Shot(clean, "03-start-no-collision");
        LogDialog(log, "03 start with no collision (client-only fleet)", clean);
        clean.Close();

        // Scherm 7 — switching, with the two steps shown.
        var switching = new SwitchFleetWindow(SwitchPrompt(now));
        switching.Show();
        Shot(switching, "04-switch");
        LogDialog(log, "04 switch dialog", switching);
        switching.Close();

        // Scherm 7 — the request as it lands in the inbox, beside an ordinary invite.
        {
            var (instance, inbox) = await BuildInboxAsync();
            using (instance)
            {
                var window = new InboxWindow(inbox) { Width = 520, Height = 520 };
                window.Show();
                Shot(window, "05-inbox-switch-request");
                LogDialog(log, "05 inbox with a switch request", window);
                window.Close();
            }
        }

        // Scherm 1 — the member row's switch and the band's, on the fleet the collision happened in.
        {
            var (instance, vm, _) = await BuildOverviewAsync();
            using (instance)
            {
                foreach (var row in vm.ActiveFleets)
                    row.ToggleExpandedCommand.Execute(null);
                var window = new FleetsWindow(vm) { Width = 1578, Height = 900 };
                window.Show();
                Shot(window, "06-overview-wide");
                LogOverview(log, "06 overview wide 1578", vm);
                window.Width = 758; window.Height = 720;
                Shot(window, "07-overview-narrow");
                LogOverview(log, "07 overview narrow 758", vm);
                window.Close();
                vm.Dispose();
            }
        }

        Directory.CreateDirectory(OutDir);
        File.WriteAllText(Path.Combine(OutDir, "log.txt"), log.ToString());
    }
}
