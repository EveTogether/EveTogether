using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Imaging;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.Views;

/// <summary>Character-picker dialog. Single mode returns the chosen character id (<c>int?</c>); multi mode returns the
/// chosen ids (<c>IReadOnlyList&lt;int&gt;</c>) so an action can run for several characters at once. Null on cancel.</summary>
public partial class CharacterPickerWindow : ChromedWindow
{
    private readonly bool _multiSelect;
    private PixelPoint? _lastPosition;

    public ObservableCollection<CharacterPickRowViewModel> Options { get; } = [];

    /// <summary>Whether more than one row can be picked — bound by the row template to switch its selection mark
    /// between a checkbox (many) and a radio dot (one), so which mode this dialog is in is visible before anything
    /// is clicked (ET-184).</summary>
    public bool IsMultiSelect => _multiSelect;

    public CharacterPickerWindow()
    {
        AvaloniaXamlLoader.Load(this);
        // ET-207: this prompt fires from a ctrl+c on a site while EVE is fullscreen, before any activity window
        // exists — DialogService's own "raise it over the run overlay" rule (_Over) never fires for that case, so
        // the dialog owns always-on-top itself rather than depending on it. Set before Show() (not in OnOpened) so
        // there is no frame where the dialog paints behind the client and then jumps in front of it.
        Topmost = true;
        PositionChanged += (_, e) => _lastPosition = e.Point;
    }

    public CharacterPickerWindow(string prompt, IReadOnlyList<CharacterPickOption> options, bool multiSelect = false) : this()
    {
        _multiSelect = multiSelect;
        // Set in code-behind: an ElementName binding to a plain property reads "" at load time (assigned after).
        this.FindControl<TextBlock>("PromptText")!.Text =
            multiSelect ? $"{prompt}\n(pick one or more)" : prompt;
        var list = this.FindControl<ListBox>("OptionList")!;
        // Multiple alone only allows more than one selected item — it says nothing about how you select. Toggle is
        // what makes a plain click add/remove a row instead of replacing the whole selection with it (ET-186).
        if (multiSelect)
            list.SelectionMode = SelectionMode.Multiple | SelectionMode.Toggle;
        list.SelectionChanged += OnSelectionChanged;

        foreach (var o in options)
            Options.Add(new CharacterPickRowViewModel(o));

        // Program.Services is only wired in the real app (Program.Main), not in headless tests that new up this
        // window directly. One fire-and-forget load per row (not awaited in sequence), same as the fleet roster's
        // member leaves — a portrait that never loads just leaves that row on its initial-glyph fallback.
        if (Program.Services?.GetService<ICharacterPortraitProvider>() is { } portraits)
            foreach (var row in Options)
                _ = row.LoadPortraitAsync(portraits);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _ = _RestorePositionAsync();
    }

    // Runs after Show() places the window at CenterOwner (the default WindowStartupLocation still applies for the
    // very first open, AC-5), then jumps it to the remembered spot if there is one — same order OverlayWindow's own
    // RestoreAsync uses for the DPS/fleet pop-outs.
    private async Task _RestorePositionAsync()
    {
        var geometry = await OverlayGeometryStore.LoadAsync(OverlayGeometryStore.ForCharacterPicker());
        if (geometry is not { HasPosition: true }) return;

        var remembered = new PixelPoint(geometry.X, geometry.Y);
        // A remembered spot can sit on a monitor that has since been unplugged (AC-4); centered-on-owner (the
        // startup default, still in effect since nothing has moved the window yet) beats an invisible dialog.
        if (WindowChrome.IsPositionOnScreen(this, remembered))
            Position = remembered;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_lastPosition is { } position)
            _ = OverlayGeometryStore.SaveAsync(OverlayGeometryStore.ForCharacterPicker(),
                new OverlayGeometry { HasPosition = true, X = position.X, Y = position.Y });
    }

    // Mirrors the ListBox's own selection onto each row, rather than reaching into the ListBoxItem container from
    // inside its data template: the row's checkbox/radio mark and card styling bind to this directly (ET-184).
    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        foreach (var removed in e.RemovedItems.OfType<CharacterPickRowViewModel>())
            removed.IsSelected = false;
        foreach (var added in e.AddedItems.OfType<CharacterPickRowViewModel>())
            added.IsSelected = true;
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        // FindControl: the x:Name field isn't generated when loading via AvaloniaXamlLoader.Load.
        var list = this.FindControl<ListBox>("OptionList");
        if (list is null)
            return;

        if (_multiSelect)
        {
            var chosen = list.SelectedItems?.OfType<CharacterPickRowViewModel>().Where(o => o.Enabled).Select(o => o.CharacterId).ToList();
            if (chosen is { Count: > 0 })
                Close((IReadOnlyList<int>)chosen);
            return; // nothing valid selected → keep the dialog open
        }

        if (list.SelectedItem is CharacterPickRowViewModel { Enabled: true } picked)
            Close((int?)picked.CharacterId);
        // else: no (valid) selection — keep the dialog open instead of crashing.
    }
}
