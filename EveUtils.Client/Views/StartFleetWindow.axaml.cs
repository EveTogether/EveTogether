using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using EveUtils.Client.Dialogs;

namespace EveUtils.Client.Views;

/// <summary>
/// Starting a fleet, and what happens when a member is already flying somewhere else (ET-168, scherm 2). It says
/// what starting does, shows the roster it will link, and — when there is a collision — puts it in <b>one summary
/// line with one button</b>, whether one member is elsewhere or fifty. Nothing is decided per member here: starting
/// is one act, and a member-by-member form is what turns two collisions into paperwork and eleven into a reason not
/// to start at all.
///
/// <para>The button asks; it never moves anybody. "Leave them" is the default and is <i>not</i> the same as taking
/// them off the roster — they stay members, merely not linked, so a switch an hour later still joins them in.</para>
///
/// Returns the chosen <see cref="FleetStartChoice"/>, or <see cref="FleetStartChoice.Cancel"/> when the dialog is
/// closed without starting. Values are set in code-behind (the x:Name field isn't generated under
/// AvaloniaXamlLoader.Load — see CharacterPickerWindow).
/// </summary>
public partial class StartFleetWindow : ChromedWindow
{
    private FleetStartChoice _collisionChoice = FleetStartChoice.LeaveThem;

    /// <summary>The roster the dialog draws — filled by the constructor, so the list is bound and not built here.</summary>
    public ObservableCollection<FleetStartMember> Members { get; } = [];

    public StartFleetWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public StartFleetWindow(FleetStartPrompt prompt) : this()
    {
        ArgumentNullException.ThrowIfNull(prompt);

        this.FindControl<TextBlock>("FleetNameChip")!.Text = prompt.FleetName;
        foreach (var member in prompt.Members)
            Members.Add(member);

        this.FindControl<TextBlock>("MembersLabel")!.Text = DescribeRoster(prompt);
        this.FindControl<TextBlock>("WillLinkText")!.Text = DescribeWhatStarts(prompt);

        // The ESI seam only makes sense while there is anyone it could apply to.
        this.FindControl<StackPanel>("EsiBlock")!.IsVisible = prompt.ExternalCount > 0;

        if (prompt.HasCollision)
            ApplyCollision(prompt);

        ApplyCollisionChoice();
    }

    private void ApplyCollision(FleetStartPrompt prompt)
    {
        this.FindControl<StackPanel>("CollisionBlock")!.IsVisible = true;
        this.FindControl<TextBlock>("CollisionHead")!.Text = prompt.ElsewhereCount == 1
            ? "1 member is already in another active fleet"
            : string.Create(CultureInfo.InvariantCulture, $"{prompt.ElsewhereCount} members are already in another active fleet");
        this.FindControl<TextBlock>("CollisionOfChip")!.Text =
            string.Create(CultureInfo.InvariantCulture, $"of the {prompt.AvailableCount} with a client");

        // Your own alt is the one case this dialog does not ask about: moving your own character is owning it, not
        // commanding the fleet, so it is pointed at the member row rather than folded into the ask.
        if (prompt.MyAltsElsewhere.Count > 0)
        {
            var note = this.FindControl<TextBlock>("OwnAltsNote")!;
            var names = string.Join(", ", prompt.MyAltsElsewhere.Select(m => m.Name));
            note.IsVisible = true;
            note.Text = prompt.MyAltsElsewhere.Count == 1
                ? $"1 of them is your own pilot ({names}) — that one you can move yourself, from the member row in the overview."
                : string.Create(CultureInfo.InvariantCulture,
                    $"{prompt.MyAltsElsewhere.Count} of them are your own pilots ({names}) — those you can move yourself, from the member row in the overview.");
        }

        // A client-only fleet's pilots are all your own: there is no inbox to send a request to, so the button says
        // why it is off rather than quietly doing nothing.
        if (!prompt.CanAskThemAll)
        {
            var ask = this.FindControl<Button>("AskButton")!;
            ask.IsEnabled = false;
            ToolTip.SetTip(ask, "These are your own pilots — move them from the member row instead of asking them.");
            ToolTip.SetShowOnDisabled(ask, true);
        }
    }

    /// <summary>"6 on the roster · 5 with a client · 3 yours · 1 signed off" — the header over the member list.
    /// The signed-off tail only appears when it applies — most fleets have never had one.</summary>
    private static string DescribeRoster(FleetStartPrompt prompt)
    {
        var head = string.Create(CultureInfo.InvariantCulture,
            $"MEMBERS — {prompt.RosterCount} on the roster · {prompt.AvailableCount} with a client · {prompt.MineCount} yours");
        return prompt.SignedOffCount == 0
            ? head
            : head + string.Create(CultureInfo.InvariantCulture, $" · {prompt.SignedOffCount} signed off");
    }

    /// <summary>What pressing START actually achieves, in members rather than in states.</summary>
    private static string DescribeWhatStarts(FleetStartPrompt prompt)
    {
        var linked = prompt.WillLinkCount == 1
            ? "1 member is free and will be linked."
            : string.Create(CultureInfo.InvariantCulture, $"{prompt.WillLinkCount} members are free and will be linked.");

        if (prompt.SignedOffCount > 0)
            linked += prompt.SignedOffCount == 1
                ? " 1 member signed off this start and will not be linked — not a collision, they said so themselves."
                : string.Create(CultureInfo.InvariantCulture,
                    $" {prompt.SignedOffCount} members signed off this start and will not be linked — not a collision, they said so themselves.");

        if (prompt.ExternalCount == 0)
            return linked;

        return prompt.ExternalCount == 1
            ? linked + " 1 external pilot has no client of their own and shares nothing either way."
            : string.Create(CultureInfo.InvariantCulture,
                $"{linked} {prompt.ExternalCount} external pilots have no client of their own and share nothing either way.");
    }

    private void OnAskThemAll(object? sender, RoutedEventArgs e)
    {
        _collisionChoice = FleetStartChoice.AskThemAll;
        ApplyCollisionChoice();
    }

    private void OnLeaveThem(object? sender, RoutedEventArgs e)
    {
        _collisionChoice = FleetStartChoice.LeaveThem;
        ApplyCollisionChoice();
    }

    /// <summary>Which of the two picks reads as pressed, and what the footer chip promises the START will do. The
    /// class is swapped rather than a Background set locally — a local value beats a style setter.</summary>
    private void ApplyCollisionChoice()
    {
        var asking = _collisionChoice == FleetStartChoice.AskThemAll;
        this.FindControl<Button>("AskButton")?.Classes.Set("on", asking);
        this.FindControl<Button>("LeaveButton")?.Classes.Set("on", !asking);

        if (this.FindControl<Border>("CollisionChip") is not { } chip
            || this.FindControl<TextBlock>("CollisionChipText") is not { } text
            || this.FindControl<StackPanel>("CollisionBlock") is not { IsVisible: true })
            return;

        var count = Members.Count(m => m.IsElsewhereActive);
        chip.IsVisible = true;
        text.Text = string.Create(CultureInfo.InvariantCulture,
            $"{count} active elsewhere · {(asking ? "a request goes to them" : "left where they are")}");
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(FleetStartChoice.Cancel);

    /// <summary>Starting always starts. What the choice decides is only whether a request goes out with it — and
    /// with no collision at all there is nothing to ask, so it is the plain "leave them".</summary>
    private void OnConfirm(object? sender, RoutedEventArgs e) =>
        Close(Members.Any(m => m.IsElsewhereActive) ? _collisionChoice : FleetStartChoice.LeaveThem);
}
