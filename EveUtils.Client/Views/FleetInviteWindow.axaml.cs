using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Imaging;
using EveUtils.Shared.Modules.Fleet.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.Views;

/// <summary>
/// Invite dialog: pick a connected character, the role to grant on accept, and an optional free-text
/// message. Returns a <see cref="FleetInviteResult"/> on confirm, null on cancel. Values are set + read in
/// code-behind (the x:Name field isn't generated under AvaloniaXamlLoader.Load — see CharacterPickerWindow).
/// </summary>
public partial class FleetInviteWindow : ChromedWindow
{
    public ObservableCollection<CharacterPickRowViewModel> Options { get; } = [];

    public FleetInviteWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public FleetInviteWindow(string fleetName, IReadOnlyList<CharacterPickOption> options) : this()
    {
        this.FindControl<TextBlock>("HeaderBlock")!.Text = $"Invite to '{fleetName}'";
        var list = this.FindControl<ListBox>("OptionList")!;
        list.SelectionChanged += OnSelectionChanged;

        foreach (var option in options)
            Options.Add(new CharacterPickRowViewModel(option));

        // Same hex-portrait treatment as CharacterPickerWindow (ET-184): Program.Services is only wired in the
        // real app, not in headless tests that new up this window directly.
        if (Program.Services?.GetService<ICharacterPortraitProvider>() is { } portraits)
            foreach (var row in Options)
                _ = row.LoadPortraitAsync(portraits);
    }

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
        if (this.FindControl<ListBox>("OptionList")?.SelectedItem is not CharacterPickRowViewModel { Enabled: true } chosen)
            return; // no (valid) selection — keep the dialog open

        var role = (FleetRole)(this.FindControl<ComboBox>("RoleBox")?.SelectedIndex ?? (int)FleetRole.SquadMember);
        var message = this.FindControl<TextBox>("MessageBox")?.Text?.Trim();

        Close(new FleetInviteResult(
            chosen.CharacterId,
            role,
            string.IsNullOrWhiteSpace(message) ? null : message));
    }
}
