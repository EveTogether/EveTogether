using System.Collections.Generic;
using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using EveUtils.Client.Dialogs;
using EveUtils.Shared.Modules.Fleet.Entities;

namespace EveUtils.Client.Views;

/// <summary>
/// Invite dialog: pick a connected character, the role to grant on accept, and an optional free-text
/// message. Returns a <see cref="FleetInviteResult"/> on confirm, null on cancel. Values are set + read in
/// code-behind (the x:Name field isn't generated under AvaloniaXamlLoader.Load — see CharacterPickerWindow).
/// </summary>
public partial class FleetInviteWindow : ChromedWindow
{
    public ObservableCollection<CharacterPickOption> Options { get; } = [];

    public FleetInviteWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public FleetInviteWindow(string fleetName, IReadOnlyList<CharacterPickOption> options) : this()
    {
        this.FindControl<TextBlock>("HeaderBlock")!.Text = $"Invite to '{fleetName}'";
        foreach (var option in options)
            Options.Add(option);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        if (this.FindControl<ListBox>("OptionList")?.SelectedItem is not CharacterPickOption { Enabled: true } chosen)
            return; // no (valid) selection — keep the dialog open

        var role = (FleetRole)(this.FindControl<ComboBox>("RoleBox")?.SelectedIndex ?? (int)FleetRole.SquadMember);
        var message = this.FindControl<TextBox>("MessageBox")?.Text?.Trim();

        Close(new FleetInviteResult(
            chosen.CharacterId,
            role,
            string.IsNullOrWhiteSpace(message) ? null : message));
    }
}
