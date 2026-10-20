using System;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using EveUtils.Client.Dialogs;
using EveUtils.Client.ViewModels.Runs;

namespace EveUtils.Client.Views;

/// <summary>The loot table (ET-519), hosted like the other feature modules: a docked tab when docked, its own window when
/// floating, so it can stay open beside the run window.</summary>
public partial class ActivityLootWindow : ChromedWindow, IHostableModuleWindow
{
    public Action? CloseRequested { get; set; }

    public ActivityLootWindow() => AvaloniaXamlLoader.Load(this);

    public ActivityLootWindow(LootTableViewModel viewModel) : this() => DataContext = viewModel;

    // Esc closes the window (ET-521). Two doors for one rule: the window gets the key while it floats, and the content gets it
    // while it is a docked tab, where the window is hidden and holds no focus.
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        _CloseOnEscape(e);
    }

    private void OnContentKeyDown(object? sender, KeyEventArgs e) => _CloseOnEscape(e);

    private void _CloseOnEscape(KeyEventArgs e)
    {
        if (e.Handled || e.Key is not Key.Escape)
            return;

        e.Handled = true;
        // A search being typed in the picker is cleared first, like the search of the runs overview.
        if (DataContext is LootTableViewModel { IsPicking: true, SearchText.Length: > 0 } picking)
        {
            picking.SearchText = string.Empty;
            return;
        }

        if (CloseRequested is not null)
            CloseRequested();
        else
            Close();
    }
}
