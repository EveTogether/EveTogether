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
using EveUtils.Client.Theming;
using EveUtils.Client.Transport;
using EveUtils.Client.ViewModels;
using EveUtils.Client.ViewModels.Fleets;
using EveUtils.Client.Views;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Enums;
using EveUtils.Shared.Transport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-170: JOIN belongs on the fleet row itself when it fits there — visible and greyed out rather than one click
/// deeper — and folds into the "⋯" when it does not (Jithran, 2026-09-04: <i>"als het past zou de join in de rij het
/// beste zijn"</i>). "When it fits" is a width sum, not a rule of thumb, so these tests hold the two halves of that
/// sum honest:
///
/// <list type="bullet">
/// <item>every constant in <see cref="FleetRowActionWidths"/> against the button as it actually renders, so a change
/// to the font, the padding or a label fails here instead of quietly pushing a row onto a second line;</item>
/// <item>the outcome: no wide row breaks to two lines, JOIN stands on the rows that have room, and where it does not
/// it is still reachable and still says why it is off.</item>
/// </list>
/// </summary>
public class FleetRowActionWidthTests
{
    private const string Server = "krahwinkel-it.nl:7443";
    private const int Me = 95300001, Alt = 95300002, Stranger = 96300001;

    private static FleetInfo Fleet(long id, string name, int owner, FleetActivation activation, FleetVisibility visibility) =>
        new(id, name, null, visibility, FleetState.Active, owner, null, null, DateTimeOffset.UtcNow.AddDays(-3),
            activation, ActivatedAt: activation == FleetActivation.Forming ? null : DateTimeOffset.UtcNow.AddHours(-1));

    private static FleetMemberInfo Member(long id, int characterId, bool fc = false) =>
        new(id, characterId, fc ? -1 : 0, 0, fc ? FleetRole.FleetCommander : FleetRole.SquadMember, false, null, null,
            default, DateTimeOffset.UtcNow);

    /// <summary>
    /// Four server fleets, chosen so that between them every action label the row can draw is rendered at least
    /// once: my own public and invite-only fleets standing by, someone else's started public fleet I fly in, and a
    /// finished fleet of mine.
    /// </summary>
    private static async Task<(TestClientInstance Instance, FleetsViewModel Vm)> BuildAsync()
    {
        var transport = new RecordingFleetTransportClient();
        transport.MyFleetsByServer[Server] =
        [
            Fleet(31, "Mine public", Me, FleetActivation.Forming, FleetVisibility.Public),
            Fleet(32, "Mine invite only", Me, FleetActivation.Forming, FleetVisibility.InviteOnly),
            Fleet(33, "Theirs public", Stranger, FleetActivation.Active, FleetVisibility.Public),
            Fleet(34, "Mine finished", Me, FleetActivation.Concluded, FleetVisibility.InviteOnly),
        ];
        transport.MembersByFleet[31] = [Member(1, Me, fc: true)];
        transport.MembersByFleet[32] = [Member(2, Me, fc: true)];
        transport.MembersByFleet[33] = [Member(3, Stranger, fc: true), Member(4, Me)];
        transport.MembersByFleet[34] = [Member(5, Me, fc: true)];
        transport.OpenFleetsByServer[Server] = [];

        var instance = TestClientInstance.Create(services =>
        {
            services.AddSingleton<IFleetTransportClient>(transport);
            services.AddSingleton<IDialogService>(new RecordingDialogService());
            services.AddSingleton<IExternalCharacterLookup>(new FakeExternalLookup { [Stranger] = "Aurel Vantis" });
        });
        instance.Services.GetRequiredService<IThemeService>().Apply(FactionTheme.Gallente);

        var registry = instance.Services.GetRequiredService<ICharacterRegistry>();
        await registry.AddOrUpdateAsync(new Character("Jithran", Me));
        await registry.AddOrUpdateAsync(new Character("Abnoba Auscent", Alt));
        var sessions = instance.Services.GetRequiredService<IClientSessionStore>();
        await sessions.SaveAsync(Server, new ClientSessionTokens("t", "r", "Jithran", Me));
        // A second character signed in on the same server and in none of these fleets, so bringing an alt in is a
        // live action rather than an explanation — that is the state JOIN has to look right in.
        await sessions.SaveAsync(Server, new ClientSessionTokens("t", "r", "Abnoba Auscent", Alt));

        var vm = new FleetsViewModel(instance.Services, runClock: false);
        for (var i = 0; i < 300 && vm.ServerGroups.Sum(g => g.Fleets.Count) < 4; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }

        Dispatcher.UIThread.RunJobs();
        return (instance, vm);
    }

    private static void Settle(Window window)
    {
        for (var i = 0; i < 12; i++)
            Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        for (var i = 0; i < 4; i++)
            Dispatcher.UIThread.RunJobs();
    }

    /// <summary>The actions cell of every fleet row on screen, by the fleet's name.</summary>
    private static List<(string Fleet, Border Cell, List<Button> Buttons)> ActionCells(Control root)
    {
        var cells = new List<(string, Border, List<Button>)>();
        foreach (var cell in root.GetVisualDescendants().OfType<Border>()
                     .Where(b => b.Classes.Contains("cell") && b.Classes.Contains("acts") && b.IsVisible))
        {
            if (cell.Parent is Grid head && head.Parent is Border headBorder
                && (headBorder.Classes.Contains("gridhead") || headBorder.Classes.Contains("subhead")))
                continue;

            string? name = cell.FindAncestorOfType<Grid>()?.GetVisualDescendants().OfType<TextBlock>()
                .FirstOrDefault(t => t.Classes.Contains("nm"))?.Text;
            if (name is null)
                continue;   // a member sub-row, not a fleet row

            cells.Add((name, cell, cell.GetVisualDescendants().OfType<Button>().Where(b => b.IsVisible).ToList()));
        }

        return cells;
    }

    /// <summary>Width plus the margin that separates it from the next pick — what the row's arithmetic counts.</summary>
    private static double Cost(Button button) => button.Bounds.Width + button.Margin.Left + button.Margin.Right;

    private static Button ByLabel(IEnumerable<(string Fleet, Border Cell, List<Button> Buttons)> cells, string fleet, string label) =>
        Assert.Single(cells.Single(c => c.Fleet == fleet).Buttons, b => (b.Content as string) == label);

    [AvaloniaFact]
    public async Task MeasuredActionWidths_MatchTheButtonsAsTheyRender()
    {
        var (instance, vm) = await BuildAsync();
        using (instance)
        {
            vm.ToggleFinishedCommand.Execute(null);   // the finished group is folded by default; DISBAND lives there
            var window = new FleetsWindow(vm) { Width = 1578, Height = 900 };
            window.Show();
            Settle(window);

            // REQUEST is the one label the placement rule keeps off the row on this fixture, so it is put there by
            // hand to be measured. What is under test here is the button's width, not where it belongs.
            foreach (var row in vm.StandingByFleets)
                row.JoinOnRow = true;
            Settle(window);

            var cells = ActionCells((Control)window.Content!);
            Assert.Equal(4, cells.Count);

            Assert.Equal(FleetRowActionWidths.Start, Cost(ByLabel(cells, "Mine public", "START")));
            Assert.Equal(FleetRowActionWidths.Join, Cost(ByLabel(cells, "Mine public", "JOIN")));
            Assert.Equal(FleetRowActionWidths.Manage, Cost(ByLabel(cells, "Mine public", "MANAGE")));
            Assert.Equal(FleetRowActionWidths.Share, Cost(ByLabel(cells, "Mine public", "SHARE")));
            Assert.Equal(FleetRowActionWidths.Request, Cost(ByLabel(cells, "Mine invite only", "REQUEST")));
            Assert.Equal(FleetRowActionWidths.Stop, Cost(ByLabel(cells, "Theirs public", "STOP")));
            Assert.Equal(FleetRowActionWidths.View, Cost(ByLabel(cells, "Theirs public", "VIEW")));
            Assert.Equal(FleetRowActionWidths.Metrics, Cost(ByLabel(cells, "Theirs public", "METRICS")));
            Assert.Equal(FleetRowActionWidths.Leave, Cost(ByLabel(cells, "Theirs public", "LEAVE")));
            Assert.Equal(FleetRowActionWidths.Disband, Cost(ByLabel(cells, "Mine finished", "DISBAND")));

            // The "⋯" carries an icon rather than a label, so it is the one button without content.
            var overflow = cells.SelectMany(c => c.Buttons).First(b => b.Content is not string);
            Assert.Equal(FleetRowActionWidths.Overflow, Cost(overflow));

            // And every cell really is as wide as the arithmetic was told.
            Assert.All(cells, c => Assert.Equal(vm.ActionsWidth, c.Cell.Bounds.Width));

            window.Close();
            vm.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task OnAWideRow_JoinStandsWhereItFits_AndFoldsWithItsReasonWhereItDoesNot()
    {
        var (instance, vm) = await BuildAsync();
        using (instance)
        {
            var window = new FleetsWindow(vm) { Width = 1578, Height = 900 };
            window.Show();
            Settle(window);

            var mine = vm.ServerGroups.SelectMany(g => g.Fleets).Single(f => f.Name == "Mine invite only");
            var theirs = vm.ServerGroups.SelectMany(g => g.Fleets).Single(f => f.Name == "Theirs public");

            // Maximised there is width to spare, so the actions cell has grown past its 250 px floor and even the
            // heaviest offer keeps its place: START · REQUEST · MANAGE · SHARE · "⋯" is 254 against 320.
            Assert.Equal(FleetOverviewLayout.MaxActionsWidth, vm.ActionsWidth);
            Assert.True(theirs.ShowJoin);
            Assert.True(mine.ShowRequest);
            Assert.DoesNotContain(mine.OverflowItems, i => i.Label.StartsWith("REQUEST", StringComparison.Ordinal));
            AssertNoRowBreaks(window);

            // At the breakpoint the cell is back to the 250 px that holds scherm 1's four buttons, and there is no
            // room left for REQUEST's 63 — nothing scherm 1 draws may step aside for it, so it folds. JOIN's 42 on
            // someone else's fleet still fits at 239, so that one keeps its place: this is a fit, not a rule.
            window.Width = 1200;   // wide, but with nothing to spare once the fleet name is served
            Settle(window);

            Assert.Equal(FleetOverviewLayout.MinActionsWidth, vm.ActionsWidth);
            Assert.True(theirs.ShowJoin);
            Assert.False(mine.ShowRequest);
            Assert.True(mine.ShowShareButton);
            var folded = Assert.Single(mine.OverflowItems, i => i.Label.StartsWith("REQUEST", StringComparison.Ordinal));
            Assert.NotNull(folded.Command);   // an alt is free, so it is an action and not an explanation
            Assert.Equal(mine.JoinHint, folded.Tooltip);
            AssertNoRowBreaks(window);

            window.Close();
            vm.Dispose();
        }
    }

    /// <summary>No fleet row may lay its actions out over two lines — the condition the whole measurement exists
    /// to protect.</summary>
    private static void AssertNoRowBreaks(Window window)
    {
        foreach (var (fleet, _, buttons) in ActionCells((Control)window.Content!))
        {
            int rows = buttons.Select(b => Math.Round(((Visual)b).TranslatePoint(new Point(0, 0), window)?.Y ?? 0))
                .Distinct().Count();
            Assert.True(rows == 1, $"{fleet} laid its actions out over {rows} rows");
        }
    }

    [AvaloniaFact]
    public async Task OnANarrowRow_JoinAlwaysFolds_AndKeepsTheReasonItIsOff()
    {
        var (instance, vm) = await BuildAsync();
        using (instance)
        {
            var window = new FleetsWindow(vm) { Width = 758, Height = 720 };
            window.Show();
            Settle(window);

            // At 758 the row is two buttons and an overflow (scherm 10, scherm 15), whatever the arithmetic says.
            foreach (var row in vm.ServerGroups.SelectMany(g => g.Fleets))
            {
                Assert.False(row.ShowJoin);
                Assert.False(row.ShowRequest);
            }

            var theirs = vm.ServerGroups.SelectMany(g => g.Fleets).Single(f => f.Name == "Theirs public");
            var folded = Assert.Single(theirs.OverflowItems, i => i.Label.StartsWith("JOIN", StringComparison.Ordinal));
            Assert.Equal(theirs.JoinHint, folded.Tooltip);

            window.Close();
            vm.Dispose();
        }
    }
}
