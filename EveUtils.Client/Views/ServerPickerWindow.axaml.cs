using System.Collections.Generic;
using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using EveUtils.Client.Dialogs;

namespace EveUtils.Client.Views;

/// <summary>Server-picker dialog. Returns the chosen server address, or null on cancel.</summary>
public partial class ServerPickerWindow : ChromedWindow
{
    public ObservableCollection<ServerPickOption> Options { get; } = [];

    public ServerPickerWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public ServerPickerWindow(string prompt, IReadOnlyList<ServerPickOption> options) : this()
    {
        // Set in code-behind: an ElementName binding to a plain property reads "" at load time (assigned after).
        this.FindControl<TextBlock>("PromptText")!.Text = prompt;
        foreach (var o in options)
            Options.Add(o);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        var list = this.FindControl<ListBox>("OptionList");
        if (list?.SelectedItem is ServerPickOption chosen)
            Close(chosen.Address);
        // else: no selection — keep the dialog open.
    }
}
