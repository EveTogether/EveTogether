using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Client.Views.Runs;
using EveUtils.Shared.Modules.Runs.Enums;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-503: where a loot line's price came from (BP APPRAISAL, LIVE) is a badge beside the name, so that line's quantity
/// and ISK stay in the columns every other line lines up in — and so does ET-502's "Open in appraisal" link. Headless,
/// measured on the rendered rows.
/// </summary>
public sealed class LootLineBadgeAlignmentTests
{
    [AvaloniaFact]
    public void BlueprintAppraisalLine_KeepsQuantityAndIskInTheColumnsOfANormalLine()
    {
        var normal = new ActivityLootLineViewModel(34, "Overseer Sabik Vial", 4, 1_000_000m, LootKind.Gained);
        var blueprint = new ActivityLootLineViewModel(85957, "Reactive Armor Hardener Blueprint", 1, 1_149_230m, LootKind.Gained,
            isBlueprintAppraisal: true) { OpenInAppraisalCommand = new RelayCommand(() => { }) };
        var live = new ActivityLootLineViewModel(35, "Augmentation Decryptor", 1, 673_279m, LootKind.Gained, isLivePrice: true);
        (Window window, List<ActivityLootLineView> rows) = _Show(normal, blueprint, live);

        (double Quantity, double Isk) expected = _RightEdges(rows[0], window);
        Assert.Equal(expected, _RightEdges(rows[1], window));
        Assert.Equal(expected, _RightEdges(rows[2], window));
        Assert.True(_Badge(rows[1], "AppraisalBadge").IsVisible);
        Assert.True(rows[1].GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "OpenInAppraisalLink").IsVisible);
        Assert.True(_Badge(rows[2], "LiveBadge").IsVisible);
        Assert.False(_Badge(rows[0], "AppraisalBadge").IsVisible || _Badge(rows[0], "LiveBadge").IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void BlueprintAppraisalLine_CarriesNoMarkerInItsFigures_AndExplainsItselfInTheBadge()
    {
        var blueprint = new ActivityLootLineViewModel(85957, "Reactive Armor Hardener Blueprint", 1, 1_149_230m, LootKind.Gained,
            isBlueprintAppraisal: true);
        (Window window, List<ActivityLootLineView> rows) = _Show(blueprint);

        Assert.Equal(new[] { "1×", "1,149,230" }, _MoneyCells(rows[0]).Select(cell => cell.Text));
        Assert.Contains("1 run at ME 0", (string?)ToolTip.GetTip(_Badge(rows[0], "AppraisalBadge")));
        window.Close();
    }

    [AvaloniaFact]
    public void CompactTopItem_SaysABlueprintIsAppraised()
    {
        Assert.True(new CompactLootLineViewModel("Reactive Armor Hardener Blueprint", 1, 1_149_230m, isBlueprintAppraisal: true)
            .IsBlueprintAppraisal);
        Assert.False(new CompactLootLineViewModel("Overseer Sabik Vial", 4, 4_000_000m).IsBlueprintAppraisal);
    }

    private static (Window Window, List<ActivityLootLineView> Rows) _Show(params ActivityLootLineViewModel[] lines)
    {
        List<ActivityLootLineView> rows = [.. lines.Select(line => new ActivityLootLineView { DataContext = line })];
        var panel = new StackPanel();
        foreach (ActivityLootLineView row in rows)
            panel.Children.Add(row);
        var window = new Window { Width = 640, Height = 300, Content = panel };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, rows);
    }

    private static (double Quantity, double Isk) _RightEdges(ActivityLootLineView row, Window window)
    {
        TextBlock[] cells = _MoneyCells(row);
        return (_RightEdge(cells[0], window), _RightEdge(cells[1], window));
    }

    private static double _RightEdge(Control cell, Window window) =>
        (cell.TranslatePoint(new Point(cell.Bounds.Width, 0), window) ?? throw new Xunit.Sdk.XunitException("cell not in window")).X;

    private static TextBlock[] _MoneyCells(ActivityLootLineView row) =>
        [.. row.GetLogicalDescendants().OfType<TextBlock>().Where(text => text.Classes.Contains("money"))];

    private static Border _Badge(ActivityLootLineView row, string name) =>
        row.GetLogicalDescendants().OfType<Border>().Single(border => border.Name == name);
}
