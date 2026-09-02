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

    public FitImportWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public FitImportWindow(IReadOnlyList<EsiFitting> fits) : this()
    {
        foreach (var fit in fits)
        {
            var choice = new FitChoiceViewModel(fit);
            choice.PropertyChanged += (_, _) => _ShowSelectionCount();
            Choices.Add(choice);
            VisibleChoices.Add(choice);
        }
        // Set here, not bound: the ElementName binding this replaces rendered the line blank (it reads the property
        // at load time, before the constructor assigns it), so nobody ever saw "tick the ones to store locally".
        this.FindControl<TextBlock>("HeaderLine")!.Text =
            $"{fits.Count} fit(s) found on EVE — tick the ones to store locally.";
        _ShowSelectionCount();
    }

    /// <summary>What the import will actually store, counted over the whole list — the number the search hides
    /// (ET-145: "Select none" only reaches the shown fits, the import reads them all).</summary>
    public string SelectionSummary => $"{Choices.Count(c => c.IsSelected)} of {Choices.Count} selected";

    /// <summary>The ticked fits, over the whole list — what <see cref="OnConfirm"/> closes with.</summary>
    public IReadOnlyList<int> SelectedFittingIds =>
        Choices.Where(c => c.IsSelected).Select(c => c.FittingId).ToList();

    private void _ShowSelectionCount()
    {
        // Set in code-behind: an ElementName binding to a plain property never sees the value assigned after Load.
        var label = this.FindControl<TextBlock>("SelectionCount");
        if (label is not null) label.Text = SelectionSummary;
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

    private void OnSelectAll(object? sender, RoutedEventArgs e) => SelectShown(true);

    private void OnSelectNone(object? sender, RoutedEventArgs e) => SelectShown(false);

    /// <summary>Ticks (or unticks) the fits the search currently shows — which is what the buttons say they do; the
    /// fits outside the filter keep whatever they had, so selections made under another term survive.</summary>
    public void SelectShown(bool selected)
    {
        foreach (var c in VisibleChoices) c.IsSelected = selected;
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void OnConfirm(object? sender, RoutedEventArgs e) => Close(SelectedFittingIds);
}
