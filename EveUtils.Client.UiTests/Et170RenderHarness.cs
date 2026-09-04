// Throwaway render harness for ET-170 — screenshots of the fleet overview in both table states, both band
// densities and both shells. Not a test that guards anything; removed before the PR is merged.
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
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
using Avalonia.Controls.Primitives;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Fleet;
using EveUtils.Shared.Modules.Fleet.Metrics;
using EveUtils.Client.Platform;
using EveUtils.Client.Theming;
using EveUtils.Client.Transport;
using EveUtils.Client.ViewModels;
using EveUtils.Client.Views;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Enums;
using EveUtils.Shared.Modules.Fleet.Repositories;
using EveUtils.Shared.Transport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

public class Et170RenderHarness
{
    private const string Server = "krahwinkel-it.nl:7443";
    private const int Ravnholt = 1001, Kaska = 1002, Torv = 1003, Bex = 1004, Deio = 1005, Nilsa = 1006;
    private const int Aurel = 900001, Tessa = 900002, Doro = 900003, Vaari = 900004, Selo = 900005, Crowd = 910000;
    private static readonly string OutDir = Environment.GetEnvironmentVariable("ET170_SHOTS") ?? @"C:\Users\info\AppData\Local\Temp\et170\renders";

    private sealed class FakePresence(HashSet<int> mine, HashSet<int> online) : ILocalCharacterPresence
    {
        public bool? IsInGame(int characterId, string? characterName) => mine.Contains(characterId) ? online.Contains(characterId) : null;
        public bool? IsInGame(int characterId) => IsInGame(characterId, null);
        public IDisposable Subscribe(Action handler) => new Nothing();
        private sealed class Nothing : IDisposable { public void Dispose() { } }
    }

    private sealed class FakeDisplay : IModuleHostDisplay
    {
        public bool IsFloating { get; set; }
        public ObservableCollection<HostTab> HostTabs { get; } = new();
        public HostTab? SelectedHostTab { get; set; }
    }

    private static FleetInfo Fleet(long id, string name, int owner, FleetActivation activation, FleetVisibility visibility,
        DateTimeOffset? activatedAt, DateTimeOffset created) =>
        new(id, name, null, visibility, FleetState.Active, owner, null, null, created, activation, ActivatedAt: activatedAt);

    private static FleetMemberInfo Member(long id, int characterId, bool fc = false, bool external = false, DateTimeOffset? lastSeen = null) =>
        new(id, characterId, fc ? -1 : 0, 0, fc ? FleetRole.FleetCommander : FleetRole.SquadMember, external, null, null, default, lastSeen);

    private static async Task<(TestClientInstance Instance, FleetsViewModel Vm, DateTimeOffset Now)> BuildAsync(int ownCount, bool fifty)
    {
        var now = DateTimeOffset.UtcNow;
        var ownNames = new Dictionary<int, string>
        {
            [Ravnholt] = "Ravnholt", [Kaska] = "Kaska Vex", [Torv] = "Torv Kesh", [Bex] = "Bex Harrow", [Deio] = "Deio Tarn", [Nilsa] = "Nilsa Orn",
        };
        for (int i = 7; i <= ownCount; i++)
            ownNames[1000 + i] = new[] { "Vela Ordin", "Kaska Vex II", "Orin Vale", "Sett Vayan", "Prau Kesh", "Ivo Lannick", "Mara Sund", "Teo Rask" }[i - 7];
        var mine = ownNames.Keys.ToHashSet();
        var online = new HashSet<int> { Ravnholt, Kaska, Torv, Bex, Nilsa, 1007, 1008, 1009 };

        var lookup = new FakeExternalLookup
        {
            [Aurel] = "Aurel Vantis", [Tessa] = "Tessa Korrin", [Doro] = "Doro Vanth", [Vaari] = "Vaari Onc", [Selo] = "Selo Kaine",
        };
        for (int i = 0; i < 60; i++)
            lookup[Crowd + i] = $"Pilot {i:00}";

        var transport = new RecordingFleetTransportClient();
        var fleets = new List<FleetInfo>
        {
            Fleet(21, "Sansha evening Otanuomi", Aurel, FleetActivation.Active, FleetVisibility.Public, now.AddHours(-2).AddMinutes(-4).AddSeconds(-8), now.AddDays(-3)),   // started before Wednesday → Bex counts here
            Fleet(22, "Sunday DED run", Ravnholt, FleetActivation.Forming, FleetVisibility.InviteOnly, now.AddDays(-4), now.AddDays(-20)),
            Fleet(23, "Wormhole night", Aurel, FleetActivation.Forming, FleetVisibility.Public, null, now.AddDays(-7)),
        };
        transport.MembersByFleet[21] = [Member(1, Aurel, fc: true, lastSeen: now), Member(2, Bex, lastSeen: now), Member(3, Tessa, lastSeen: now), Member(4, Doro, lastSeen: now.AddMinutes(-12))];
        transport.MembersByFleet[22] = [Member(5, Ravnholt, fc: true), Member(6, Kaska), Member(7, Torv), Member(8, Tessa), Member(9, Doro), Member(10, Vaari, external: true)];
        transport.MembersByFleet[23] = [Member(11, Aurel, fc: true, lastSeen: now), Member(12, Torv), .. Enumerable.Range(0, 6).Select(i => Member(13 + i, Crowd + i, lastSeen: now))];
        if (fifty)
        {
            fleets.Add(Fleet(24, "Incursion HQ · Warp to Me", Aurel, FleetActivation.Active,
                FleetVisibility.Public, now.AddHours(-2).AddMinutes(-41).AddSeconds(-7), now.AddDays(-1)));
            var crowd = new List<FleetMemberInfo> { Member(100, Aurel, fc: true, lastSeen: now), Member(101, Kaska, lastSeen: now), Member(102, Torv, lastSeen: now), Member(103, Bex, lastSeen: now) };
            for (int i = 0; i < 46; i++)
                crowd.Add(Member(110 + i, Crowd + i, external: i >= 43, lastSeen: i == 20 ? now.AddMinutes(-30) : now));
            transport.MembersByFleet[24] = crowd;
        }
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

        // Wednesday Homefronts — running for 01:26:27, Bex on the roster but linked to the earlier-started Sansha fleet.
        var wed = (await local.CreateLocalFleetAsync("Wednesday Homefronts", null, Ravnholt)).Value;
        foreach (int id in new[] { Kaska, Torv, Bex })
            await local.AddLocalCharacterAsync(wed, id, Ravnholt);
        await local.AddExternalAsync(wed, Vaari, Ravnholt);
        await local.AddExternalAsync(wed, Selo, Ravnholt);
        await local.StartFleetAsync(wed, Ravnholt);
        await Stamp(repository, wed, now.AddHours(-1).AddMinutes(-26).AddSeconds(-27));

        // Abyssal duo — standing by, last ran two days ago.
        var duo = (await local.CreateLocalFleetAsync("Abyssal duo · Deio + Nilsa", null, Deio)).Value;
        await local.AddLocalCharacterAsync(duo, Nilsa, Deio);
        await Stamp(repository, duo, now.AddDays(-2));

        // Homefront evening — concluded.
        var done = (await local.CreateLocalFleetAsync("Homefront evening 24-08", null, Ravnholt)).Value;
        await local.AddLocalCharacterAsync(done, Kaska, Ravnholt);
        await local.AddExternalAsync(done, Vaari, Ravnholt);
        await local.StartFleetAsync(done, Ravnholt);
        await local.ConcludeFleetAsync(done, Ravnholt);
        await Stamp(repository, done, now.AddDays(-11));

        // One of my own pilots in the big fleet has their sharing switched off. That is the third axis, and the one
        // reason besides "not linked" that a member may never end up behind "show all 50" (scherm 12's amber chip).
        if (fifty)
            await instance.Services.GetRequiredService<EveUtils.Shared.Cqrs.IDispatcher>()
                .Send(new EveUtils.Shared.Modules.Settings.Commands.SetSettingCommand(
                    MetricShareSnapshot.OverrideKeyFor(24, Torv, MetricKind.Dps), "false"));

        var vm = new FleetsViewModel(instance.Services, runClock: false);
        for (var i = 0; i < 200 && !(vm.LocalFleets.Count == 3 && vm.ServerGroups.Count == 1 && vm.ActiveFleets.Count >= 2); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(25);
        }
        Dispatcher.UIThread.RunJobs();
        vm.Tick(now);
        return (instance, vm, now);
    }

    private static async Task Stamp(IFleetRepository repository, long fleetId, DateTimeOffset activatedAt)
    {
        var fleet = await repository.GetAsync(fleetId);
        fleet!.ActivatedAt = activatedAt;
        await repository.UpdateAsync(fleet);
    }

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

    private static void Log(StringBuilder log, string title, FleetsViewModel vm, Control root)
    {
        log.AppendLine($"== {title}");
        log.AppendLine($"layout: {vm.Layout}");
        log.AppendLine($"header: {vm.HeaderFleetsText} | {vm.HeaderCharactersText} | lanes: {vm.LanesHeaderText}");
        foreach (var lane in vm.Lanes)
            log.AppendLine($"  lane {lane.CharacterName}: {lane.RoleChipText} | {lane.FleetText}{lane.FleetOriginText} | {lane.WhereText} | {lane.ClockText} | {lane.PrimaryActionText}/{lane.SecondaryActionText} | chips: {string.Join(", ", lane.FootChips.Select(c => c.Text))}");
        foreach (var row in vm.ActiveFleets.Concat(vm.StandingByFleets).Concat(vm.FinishedFleets))
            log.AppendLine($"  row {row.GroupStatusText} {row.Name} | {row.KindText} | {row.NarrowSubText} | n={row.MemberCountText} ({row.MemberCountSubText}) | fc={row.CommanderText} ({row.CommanderSubText}) | own={row.OwnCharactersText} / {row.OwnCharactersSubText} | {row.SinceText} / {row.SinceSubText} | expanded={row.IsExpanded} visible={row.VisibleMembers.Count} hidden={row.HiddenMemberCount} chips={string.Join(",", row.HiddenMemberChips.Select(c => c.Text))} overflow={row.OverflowItems.Count}");
        foreach (var cell in root.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("cell acts") || (b.Classes.Contains("cell") && b.Classes.Contains("acts"))))
        {
            if (cell.Parent is Grid ag && ag.Parent is Border ab && ab.Classes.Contains("gridhead"))
                continue;
            var name = cell.FindAncestorOfType<Grid>()?.GetVisualDescendants().OfType<TextBlock>()
                .FirstOrDefault(t => t.Classes.Contains("nm"))?.Text;
            var live = cell.GetVisualDescendants().OfType<Button>().Where(b => b.IsVisible).ToList();
            var buttons = live.Select(b => (b.Content as string ?? "⋯") + (b.IsEnabled ? "" : "(off)")
                + $"={b.Bounds.Width + b.Margin.Left + b.Margin.Right:0.#}").ToList();
            int buttonRows = live.Select(b => Math.Round(((Visual)b).TranslatePoint(new Point(0, 0), cell)?.Y ?? 0)).Distinct().Count();
            // What a single row of them would need: every button's own width plus the 3 px margin between two picks.
            double needed = live.Sum(b => b.Bounds.Width + b.Margin.Left + b.Margin.Right);
            log.AppendLine($"  acts [{name}] buttonRows={buttonRows} needs={needed:0.#} of w={cell.Bounds.Width:0.#} h={cell.Bounds.Height:0.#} → {string.Join(" ", buttons)}");
        }
        foreach (var cell in root.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("cell") && b.Parent is Grid g && g.Parent is Border pb && pb.Classes.Contains("gridhead")))
            log.AppendLine($"  headcell [{string.Join(" ", cell.Classes)}] w={cell.Bounds.Width:0.#} x={cell.Bounds.X:0.#} visible={cell.IsVisible}");
        foreach (var line in root.GetVisualDescendants().OfType<Panel>().Where(b => b.Classes.Contains("noteline") && b.IsVisible))
        {
            var text = line.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Classes.Contains("note"));
            log.AppendLine($"  noteline x={((Visual)line).TranslatePoint(new Point(0, 0), root)?.X:0.#} " +
                           $"textX={(text is null ? -1 : ((Visual)text).TranslatePoint(new Point(0, 0), root)?.X):0.#}");
        }
        foreach (var head in root.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("subhead") && b.IsVisible))
            foreach (var t in head.GetVisualDescendants().OfType<TextBlock>())
                log.AppendLine($"  subhead [{t.Text}] x={((Visual)t).TranslatePoint(new Point(0, 0), root)?.X:0.#}");
        foreach (var bar in root.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("toolbar")))
        {
            var wrap = bar.GetVisualDescendants().OfType<WrapPanel>().FirstOrDefault();
            var search = bar.GetVisualDescendants().OfType<TextBox>().FirstOrDefault();
            double widest = 0;
            if (wrap is not null)
                foreach (var child in wrap.Children)
                    widest = Math.Max(widest, (((Visual)child).TranslatePoint(new Point(child.Bounds.Width, 0), wrap)?.X) ?? 0);
            log.AppendLine($"  toolbar h={bar.Bounds.Height:0.#} wrap w={wrap?.Bounds.Width:0.#} h={wrap?.Bounds.Height:0.#} widestChildRight={widest:0.#} search w={search?.Bounds.Width:0.#}");
        }
        foreach (var note in root.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("whynote") && b.IsVisible))
        {
            double noteX = ((Visual)note).TranslatePoint(new Point(0, 0), root)?.X ?? -1;
            var text = note.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault();
            double textX = text is null ? -1 : ((Visual)text).TranslatePoint(new Point(0, 0), root)?.X ?? -1;
            log.AppendLine($"  whynote x={noteX:0.#} textX={textX:0.#} w={note.Bounds.Width:0.#} h={note.Bounds.Height:0.#}");
        }
        foreach (var nm in root.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("mname") && t.IsVisible).Take(1))
            log.AppendLine($"  membername x={((Visual)nm).TranslatePoint(new Point(0, 0), root)?.X:0.#}");
        var lanes = root.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("lane")).ToList();
        foreach (var lane in lanes)
        {
            var name = lane.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Classes.Contains("lanename"))?.Text;
            var foot = lane.GetVisualDescendants().OfType<Control>().FirstOrDefault(p => p.Classes.Contains("lanefoot") && p.IsVisible);
            double footWidth = foot?.Bounds.Width ?? 0;
            double chipsRight = 0;
            if (foot is not null)
                foreach (var chip in foot.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("chip")))
                    chipsRight = Math.Max(chipsRight, ((Visual)chip).TranslatePoint(new Point(chip.Bounds.Width, 0), lane)?.X ?? 0);
            log.AppendLine($"  lanecard [{name}] w={lane.Bounds.Width:0.#} h={lane.Bounds.Height:0.#} x={lane.Bounds.X:0.#} y={lane.Bounds.Y:0.#} slim={lane.Classes.Contains("slim")} foot={footWidth:0.#} chipsRight={chipsRight:0.#} overflow={Math.Max(0, chipsRight - lane.Bounds.Width):0.#}");
        }
        var band = root.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("band"));
        if (band is not null)
            log.AppendLine($"  band h={band.Bounds.Height:0.#} w={band.Bounds.Width:0.#}");
        log.AppendLine($"  root w={root.Bounds.Width:0.#} h={root.Bounds.Height:0.#}");
    }

    [AvaloniaFact]
    public async Task Render_All()
    {
        var log = new StringBuilder();
        {
            var (instance, vm, _) = await BuildAsync(6, fifty: false);
            using (instance)
            {
                vm.ActiveFleets.First(r => r.IsLocal).ToggleExpandedCommand.Execute(null);
                var window = new FleetsWindow(vm) { Width = 758, Height = 720 };
                window.Show();
                Shot(window, "01-narrow-758");
                Log(log, "01 narrow own window 758", vm, (Control)window.Content!);
                window.Width = 1578; window.Height = 900;
                Shot(window, "02-wide-1578");
                Log(log, "02 wide own window 1578", vm, (Control)window.Content!);
                // FINISHED is folded by default (scherm 1), so its row is only ever seen after a click.
                vm.ToggleFinishedCommand.Execute(null);
                Shot(window, "10-finished-open-wide");
                Log(log, "10 finished unfolded 1578", vm, (Control)window.Content!);
                window.Close();
                vm.Dispose();
            }
        }
        {
            var (instance, vm, _) = await BuildAsync(6, fifty: false);
            using (instance)
            {
                vm.ActiveFleets.First(r => r.IsLocal).ToggleExpandedCommand.Execute(null);
                var window = new FleetsWindow(vm);
                var display = new FakeDisplay();
                var host = new ModuleHostService();
                var owner = new Window { Width = 1100, Height = 720 };
                host.SetOwner(owner);
                host.SetHost(display);
                host.Open(window, "FLEETS", "fleets", "fleet");
                var content = (Control)display.HostTabs.Single().Content!;
                var root = new Window { Width = 758, Height = 606, Content = content };
                root.Show();
                Shot(root, "03-docked-758");
                Log(log, "03 docked tab 758", vm, content);
                // ⇄ float: the same content back into its own window
                root.Content = null;
                display.IsFloating = true;
                host.SwitchMode();
                Settle();
                Shot(window, "04-after-switch-float-720");
                Log(log, "04 after SwitchMode float", vm, (Control)window.Content!);
                window.Close();
                root.Close();
                vm.Dispose();
            }
        }
        {
            var (instance, vm, _) = await BuildAsync(12, fifty: false);
            using (instance)
            {
                var window = new FleetsWindow(vm) { Width = 758, Height = 720 };
                window.Show();
                Shot(window, "05-twelve-narrow-compact");
                Log(log, "05 twelve pilots narrow", vm, (Control)window.Content!);
                window.Width = 1578; window.Height = 900;
                Shot(window, "06-twelve-wide");
                Log(log, "06 twelve pilots wide", vm, (Control)window.Content!);
                window.Close();
                vm.Dispose();
            }
        }
        {
            var (instance, vm, _) = await BuildAsync(6, fifty: true);
            using (instance)
            {
                vm.ActiveFleets.First(r => r.Name.StartsWith("Incursion", StringComparison.Ordinal)).ToggleExpandedCommand.Execute(null);
                var window = new FleetsWindow(vm) { Width = 1578, Height = 900 };
                window.Show();
                Shot(window, "07-fifty-wide");
                Log(log, "07 fifty wide", vm, (Control)window.Content!);
                window.Width = 758; window.Height = 720;
                Shot(window, "08-fifty-narrow");
                Log(log, "08 fifty narrow", vm, (Control)window.Content!);
                var big = vm.ActiveFleets.First(r => r.Name.StartsWith("Incursion", StringComparison.Ordinal));
                big.ToggleMembersCommand.Execute(null);
                window.Width = 1578; window.Height = 900;
                Shot(window, "09-fifty-show-all-wide");
                Log(log, "09 fifty show all wide", vm, (Control)window.Content!);
                window.Close();
                vm.Dispose();
            }
        }
        Directory.CreateDirectory(OutDir);
        File.WriteAllText(Path.Combine(OutDir, "log.txt"), log.ToString());
    }
}
