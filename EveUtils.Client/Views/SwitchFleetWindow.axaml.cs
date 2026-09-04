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
/// Moving one of my own pilots from the fleet it counts for into another one (ET-168, scherm 7). In the code this
/// is two acts — leave, then couple — and coupling while still active elsewhere is refused outright. This screen
/// makes one button of it, and shows the two steps rather than hiding them, so that what the button does is the
/// thing that was read.
///
/// <para>Only ever one of my own characters: a commander asks other people to switch, and moves their own alt
/// because it is their character, not because they command the fleet. Returns true when the pilot goes.</para>
///
/// Values are set in code-behind (the x:Name field isn't generated under AvaloniaXamlLoader.Load — see
/// CharacterPickerWindow).
/// </summary>
public partial class SwitchFleetWindow : ChromedWindow
{
    /// <summary>The runs still going, already formatted as lines — the dialog shows them, it does not compute them.</summary>
    public ObservableCollection<string> RunsInProgress { get; } = [];

    public SwitchFleetWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public SwitchFleetWindow(SwitchFleetPrompt prompt) : this()
    {
        ArgumentNullException.ThrowIfNull(prompt);

        this.FindControl<TextBlock>("CharacterChip")!.Text = $"{prompt.CharacterName} · your character, your choice";
        this.FindControl<TextBlock>("NowValue")!.Text = DescribeNow(prompt);
        this.FindControl<TextBlock>("NextValue")!.Text = Describe(prompt.TargetFleetName, prompt.TargetActivatedAt);

        var leaving = string.Join(" and ", prompt.Leaving.Select(l => l.FleetName));
        this.FindControl<TextBlock>("StepOneHead")!.Text = prompt.Leaving.Count == 0
            ? "1 · You leave nothing."
            : $"1 · You leave {leaving}.";
        this.FindControl<TextBlock>("StepOneBody")!.Text = prompt.Leaving.Count == 0
            ? "This pilot counts for no started fleet, so there is nothing to walk out of."
            : $"That fleet keeps running for everyone else; only this pilot is no longer a member of it. If you meant to stay on its roster for next time, this is not what you want.";
        this.FindControl<TextBlock>("StepTwoHead")!.Text = $"2 · You link to {prompt.TargetFleetName}.";

        this.FindControl<TextBlock>("TwoStepsNote")!.Text =
            "In the code these are two steps — leaving, then coupling — and coupling while you are still active elsewhere "
            + "is refused with “leave or conclude it before joining another”. This button does both, in that order, "
            + "and stops before the first one if the second could not have worked.";

        foreach (var run in prompt.RunsInProgress)
            RunsInProgress.Add(run);

        if (RunsInProgress.Count > 0)
        {
            var noun = RunsInProgress.Count == 1 ? "run" : "runs";
            this.FindControl<Grid>("RunsRow")!.IsVisible = true;
            this.FindControl<TextBlock>("RunsValue")!.Text =
                string.Create(CultureInfo.InvariantCulture, $"{RunsInProgress.Count} still running");
            this.FindControl<StackPanel>("RunsBlock")!.IsVisible = true;
            this.FindControl<TextBlock>("RunsBlockLabel")!.Text = $"THE {noun.ToUpperInvariant()} STILL GOING";
        }

        // The footer says the consequence that is easiest to miss: a switch takes you off the roster you leave, and
        // only that roster. Scherm 7's own chip says "you stay on both rosters", which its step 1 contradicts.
        this.FindControl<TextBlock>("FootChipText")!.Text = prompt.Leaving.Count == 0
            ? $"you stay on {prompt.TargetFleetName}'s roster"
            : prompt.Leaving.Count == 1
                ? $"you come off {prompt.Leaving[0].FleetName}'s roster"
                : string.Create(CultureInfo.InvariantCulture, $"you come off {prompt.Leaving.Count} rosters");
    }

    private static string DescribeNow(SwitchFleetPrompt prompt) => prompt.Leaving.Count == 0
        ? "no started fleet"
        : string.Join(" · ", prompt.Leaving.Select(l => Describe(l.FleetName, l.ActivatedAt)));

    /// <summary>Invariant on purpose: this is a clock readout, and the tests run on a machine whose culture is not
    /// English (ET-34).</summary>
    private static string Describe(string fleetName, DateTimeOffset? activatedAt) => activatedAt is { } since
        ? string.Create(CultureInfo.InvariantCulture, $"{fleetName} · active since {since.ToLocalTime():HH\\:mm}")
        : $"{fleetName} · active";

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    private void OnConfirm(object? sender, RoutedEventArgs e) => Close(true);
}
