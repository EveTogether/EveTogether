using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using EveUtils.Client.Dialogs;
using EveUtils.Shared.Modules.Esi;

namespace EveUtils.Client.Views;

/// <summary>Scope-selection dialog. Returns the selected scope strings, or null on cancel.</summary>
public partial class ScopeSelectionWindow : ChromedWindow
{
    public ObservableCollection<ScopeChoiceViewModel> Choices { get; } = [];

    public ScopeSelectionWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public ScopeSelectionWindow(IReadOnlyList<EsiScopeRequirement> available,
        IReadOnlyCollection<string>? preselected = null) : this()
    {
        // No preselection (sign-in) → tick every non-opt-in scope (opt-in ones like fleet access start unticked, Q1);
        // a re-auth passes the granted scopes so only those start ticked, regardless of the opt-in flag.
        foreach (var req in available)
            Choices.Add(new ScopeChoiceViewModel(req,
                preselected is null ? !req.OptIn : preselected.Contains(req.Scope, System.StringComparer.OrdinalIgnoreCase)));
    }

    private void OnSelectAll(object? sender, RoutedEventArgs e)
    {
        foreach (var c in Choices) c.IsSelected = true;
    }

    private void OnSelectNone(object? sender, RoutedEventArgs e)
    {
        foreach (var c in Choices) c.IsSelected = false;
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        var selected = Choices.Where(c => c.IsSelected).Select(c => c.Scope).ToList();
        Close((IReadOnlyList<string>)selected);
    }
}
