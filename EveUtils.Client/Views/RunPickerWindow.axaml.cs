using System.Collections.Generic;
using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using EveUtils.Client.Dialogs;

namespace EveUtils.Client.Views;

/// <summary>Run-picker dialog. Returns the chosen run's id, or null on cancel. The best match is preselected.</summary>
public partial class RunPickerWindow : ChromedWindow
{
    public ObservableCollection<RunPickOption> Options { get; } = [];

    public RunPickerWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public RunPickerWindow(string prompt, IReadOnlyList<RunPickOption> options) : this()
    {
        // Set in code-behind: an ElementName binding to a plain property reads "" at load time (assigned after).
        this.FindControl<TextBlock>("PromptText")!.Text = prompt;
        foreach (var o in options)
            Options.Add(o);
        this.FindControl<ListBox>("OptionList")!.SelectedIndex = 0;
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        var list = this.FindControl<ListBox>("OptionList");
        if (list?.SelectedItem is RunPickOption chosen)
            Close(chosen.RunId);
    }
}
