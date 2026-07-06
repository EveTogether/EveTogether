using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using EveUtils.Client.Dialogs;
using EveUtils.Shared.Modules.Fittings.Dtos;

namespace EveUtils.Client.Views;

/// <summary>Fit-import dialog. Returns the selected ESI fitting ids, or null on cancel.</summary>
public partial class FitImportWindow : ChromedWindow
{
    // Choices holds every fit (and its selection state); VisibleChoices is the search-filtered view the list binds to,
    // so selecting fits, searching for others and selecting those too all carry through to the import.
    public ObservableCollection<FitChoiceViewModel> Choices { get; } = [];
    public ObservableCollection<FitChoiceViewModel> VisibleChoices { get; } = [];
    public string HeaderText { get; private set; } = "";

    public FitImportWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public FitImportWindow(IReadOnlyList<EsiFitting> fits) : this()
    {
        foreach (var fit in fits)
        {
            var choice = new FitChoiceViewModel(fit);
            Choices.Add(choice);
            VisibleChoices.Add(choice);
        }
        HeaderText = $"{fits.Count} fit(s) found on EVE — tick the ones to store locally.";
    }

    private void OnSearchChanged(object? sender, TextChangedEventArgs e) => ApplyFilter((sender as TextBox)?.Text);

    /// <summary>Filters the shown list to fits whose name contains the term (case-insensitive); an empty term shows all.
    /// Selection lives on <see cref="Choices"/>, so ticking fits under one search and then another all import.</summary>
    public void ApplyFilter(string? term)
    {
        var trimmed = term?.Trim() ?? "";
        VisibleChoices.Clear();
        foreach (var choice in Choices.Where(c => trimmed.Length == 0
                     || c.Name.Contains(trimmed, StringComparison.OrdinalIgnoreCase)))
            VisibleChoices.Add(choice);
    }

    private void OnSelectAll(object? sender, RoutedEventArgs e)
    {
        foreach (var c in VisibleChoices) c.IsSelected = true;
    }

    private void OnSelectNone(object? sender, RoutedEventArgs e)
    {
        foreach (var c in VisibleChoices) c.IsSelected = false;
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        var selected = Choices.Where(c => c.IsSelected).Select(c => c.FittingId).ToList();
        Close((IReadOnlyList<int>)selected);
    }
}
