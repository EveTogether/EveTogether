using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using EveUtils.Client.Dialogs;

namespace EveUtils.Client.Views;

/// <summary>Character-picker dialog. Single mode returns the chosen character id (<c>int?</c>); multi mode returns the
/// chosen ids (<c>IReadOnlyList&lt;int&gt;</c>) so an action can run for several characters at once. Null on cancel.</summary>
public partial class CharacterPickerWindow : ChromedWindow
{
    private readonly bool _multiSelect;

    public ObservableCollection<CharacterPickOption> Options { get; } = [];

    public CharacterPickerWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public CharacterPickerWindow(string prompt, IReadOnlyList<CharacterPickOption> options, bool multiSelect = false) : this()
    {
        _multiSelect = multiSelect;
        // Set in code-behind: an ElementName binding to a plain property reads "" at load time (assigned after).
        this.FindControl<TextBlock>("PromptText")!.Text =
            multiSelect ? $"{prompt}\n(pick one or more)" : prompt;
        if (multiSelect)
            this.FindControl<ListBox>("OptionList")!.SelectionMode = SelectionMode.Multiple;
        foreach (var o in options)
            Options.Add(o);
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
            var chosen = list.SelectedItems?.OfType<CharacterPickOption>().Where(o => o.Enabled).Select(o => o.CharacterId).ToList();
            if (chosen is { Count: > 0 })
                Close((IReadOnlyList<int>)chosen);
            return; // nothing valid selected → keep the dialog open
        }

        if (list.SelectedItem is CharacterPickOption { Enabled: true } picked)
            Close((int?)picked.CharacterId);
        // else: no (valid) selection — keep the dialog open instead of crashing.
    }
}
