using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Views;
using EveUtils.Shared.Modules.Fittings.Dtos;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>The ESI import dialog's search filters the shown fits by name; selection lives on the full list, so fits
/// ticked under one search term and another both survive to the import.</summary>
public class FitImportWindowTests
{
    private static int _nextId;
    private static EsiFitting Fit(string name) => new(++_nextId, name, "", 587, new List<EsiFittingItem>());

    [AvaloniaFact]
    public void Search_FiltersByName_AndKeepsSelectionAcrossTerms()
    {
        var window = new FitImportWindow(new[] { Fit("Rifter PvP"), Fit("Thorax PvE"), Fit("Rifter Shield") });
        Assert.Equal(3, window.VisibleChoices.Count);                       // all shown initially

        window.ApplyFilter("rifter");                                       // case-insensitive name match
        Assert.Equal(2, window.VisibleChoices.Count);
        Assert.All(window.VisibleChoices, c => Assert.Contains("Rifter", c.Name));
        window.VisibleChoices.First(c => c.Name == "Rifter PvP").IsSelected = true;    // tick under the filter

        window.ApplyFilter("thorax");                                       // switch term
        Assert.Equal("Thorax PvE", Assert.Single(window.VisibleChoices).Name);
        window.VisibleChoices[0].IsSelected = true;                         // and tick under the next one

        window.ApplyFilter("");                                             // clear -> all shown again
        Assert.Equal(3, window.VisibleChoices.Count);
        Assert.Equal(2, window.Choices.Count(c => c.IsSelected));           // ticked under two terms; both survived
    }

    /// <summary>ET-145, the operator's sequence: search, "Select none shown", tick one, import. What the filter hid
    /// must not ride along.</summary>
    [AvaloniaFact]
    public void ImportingAfterASearch_TakesOnlyWhatWasTicked()
    {
        var window = new FitImportWindow(new[] { Fit("Rifter PvP"), Fit("Thorax PvE"), Fit("Rifter Shield") });
        Assert.Empty(window.SelectedFittingIds);                            // nothing ticked on open

        window.ApplyFilter("rifter");
        window.SelectShown(false);                                          // "Select none shown"
        window.VisibleChoices.First(c => c.Name == "Rifter PvP").IsSelected = true;

        Assert.Equal("1 of 3 selected", window.SelectionSummary);           // the count names the whole list
        var imported = window.SelectedFittingIds;
        Assert.Equal("Rifter PvP", Assert.Single(window.Choices, c => c.IsSelected).Name);
        Assert.Single(imported);                                            // not the hidden Thorax as well
    }

    /// <summary>The same sequence through the real controls — the search box, the buttons as they are labelled and
    /// the row checkboxes — because the bug was in the wiring between them, not in any one of them.</summary>
    [AvaloniaFact]
    public void TheOperatorsSequence_ThroughTheButtonsThemselves()
    {
        var window = new FitImportWindow(new[] { Fit("Rifter PvP"), Fit("Thorax PvE"), Fit("Rifter Shield") });
        window.Show();
        Assert.Contains("3 fit(s) found on EVE — tick the ones to store locally.", RenderedText.VisibleTexts(window));

        var search = window.GetVisualDescendants().OfType<TextBox>().Single();
        search.Text = "rifter";                                             // TextChanged -> OnSearchChanged
        Dispatcher.UIThread.RunJobs();

        Button Labelled(string content) => window.GetVisualDescendants().OfType<Button>()
            .Single(b => (b.Content as string) == content);

        Labelled("Select none shown").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        var boxes = window.GetVisualDescendants().OfType<CheckBox>().ToList();
        Assert.Equal(2, boxes.Count);                                       // only the two Rifters are on screen
        boxes[0].IsChecked = true;
        Dispatcher.UIThread.RunJobs();

        // What the operator can read before pressing Import, and what Import actually takes.
        Assert.Contains("1 of 3 selected", RenderedText.VisibleTexts(window));
        Assert.Single(window.SelectedFittingIds);

        window.Close();
    }

    /// <summary>"Select all shown" stays on the shown fits — that is what the label says, and it is what lets two
    /// searches add up.</summary>
    [AvaloniaFact]
    public void SelectAllShown_AddsToWhatEarlierSearchesTicked()
    {
        var window = new FitImportWindow(new[] { Fit("Rifter PvP"), Fit("Thorax PvE"), Fit("Rifter Shield") });

        window.ApplyFilter("thorax");
        window.SelectShown(true);
        window.ApplyFilter("rifter");
        window.SelectShown(true);

        Assert.Equal(3, window.SelectedFittingIds.Count);
        Assert.Equal("3 of 3 selected", window.SelectionSummary);
    }
}
