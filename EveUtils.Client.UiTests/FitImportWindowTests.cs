using System.Collections.Generic;
using System.Linq;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Views;
using EveUtils.Shared.Modules.Fittings.Dtos;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>The ESI import dialog's search filters the shown fits by name; selection lives on the full list, so fits
/// ticked under one search term and another both survive to the import.</summary>
public class FitImportWindowTests
{
    private static EsiFitting Fit(string name) => new(0, name, "", 587, new List<EsiFittingItem>());

    [AvaloniaFact]
    public void Search_FiltersByName_AndKeepsSelectionAcrossTerms()
    {
        var window = new FitImportWindow(new[] { Fit("Rifter PvP"), Fit("Thorax PvE"), Fit("Rifter Shield") });
        Assert.Equal(3, window.VisibleChoices.Count);                       // all shown initially

        window.ApplyFilter("rifter");                                       // case-insensitive name match
        Assert.Equal(2, window.VisibleChoices.Count);
        Assert.All(window.VisibleChoices, c => Assert.Contains("Rifter", c.Name));
        window.VisibleChoices.First(c => c.Name == "Rifter PvP").IsSelected = false;   // deselect under the filter

        window.ApplyFilter("thorax");                                       // switch term
        Assert.Equal("Thorax PvE", Assert.Single(window.VisibleChoices).Name);

        window.ApplyFilter("");                                             // clear -> all shown again
        Assert.Equal(3, window.VisibleChoices.Count);
        Assert.Equal(2, window.Choices.Count(c => c.IsSelected));           // only Rifter PvP was deselected; survived the searches
    }
}
