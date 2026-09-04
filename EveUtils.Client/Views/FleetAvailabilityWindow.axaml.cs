using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using EveUtils.Client.Dialogs;
using EveUtils.Shared.Modules.Fleet.Entities;

namespace EveUtils.Client.Views;

/// <summary>
/// Signing off a Forming fleet's next start without leaving it (ET-169, scherm 7). "No, not this time" stays
/// on the roster and stops counting as available for the next start only — it is reset back to "nothing said"
/// as soon as that start happens, exactly like an unopened row already reads. Distinct from LEAVE, which this
/// dialog never offers: leaving takes the pilot off the roster for good, signing off never does.
///
/// Values are set in code-behind (the x:Name field isn't generated under AvaloniaXamlLoader.Load — see
/// CharacterPickerWindow). Returns null when cancelled.
/// </summary>
public partial class FleetAvailabilityWindow : ChromedWindow
{
    public FleetAvailabilityWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public FleetAvailabilityWindow(FleetAvailabilityPrompt prompt) : this()
    {
        this.FindControl<TextBlock>("HeaderChip")!.Text = $"{prompt.CharacterName} · {prompt.FleetName}";

        var available = this.FindControl<RadioButton>("AvailableOption")!;
        var signedOff = this.FindControl<RadioButton>("SignedOffOption")!;
        var note = this.FindControl<TextBox>("NoteBox")!;

        // NotSet reads back as "yes" — silence already counts as available, so reopening this dialog on an
        // untouched member should not look like it is asking a question that was never answered.
        if (prompt.Current == FleetMemberAvailability.SignedOff)
            signedOff.IsChecked = true;
        else
            available.IsChecked = true;
        note.Text = prompt.CurrentNote;
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        var signedOff = this.FindControl<RadioButton>("SignedOffOption")!.IsChecked == true;
        var note = this.FindControl<TextBox>("NoteBox")!.Text;
        Close(new FleetAvailabilitySubmission(
            signedOff ? FleetMemberAvailability.SignedOff : FleetMemberAvailability.Available,
            string.IsNullOrWhiteSpace(note) ? null : note.Trim()));
    }
}
