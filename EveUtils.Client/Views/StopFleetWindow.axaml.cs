using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using EveUtils.Client.Dialogs;

namespace EveUtils.Client.Views;

/// <summary>
/// The way out of an active fleet (ET-166), and the screen where the three verbs stop looking alike. Stopping is
/// the reversible one and leads the list; concluding is the terminal one and is still here, just no longer the
/// default; leaving pulls one of my own pilots out and leaves the fleet running. Deleting the fleet is deliberately
/// absent — Disband lives on the fleet overview, and having it stand next to Stop is what made stopping read as
/// dangerous. Returns the chosen <see cref="StopFleetChoice"/>, or <see cref="StopFleetChoice.Cancel"/> when the
/// dialog is closed without a decision. Values are set in code-behind (the x:Name field isn't generated under
/// AvaloniaXamlLoader.Load — see CharacterPickerWindow).
/// </summary>
public partial class StopFleetWindow : ChromedWindow
{
    public ObservableCollection<StopFleetOption> Options { get; } = [];

    /// <summary>The runs still going, already formatted as lines — the dialog shows them, it does not compute them.</summary>
    public ObservableCollection<string> RunsInProgress { get; } = [];

    public StopFleetWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public StopFleetWindow(StopFleetPrompt prompt) : this()
    {
        ArgumentNullException.ThrowIfNull(prompt);

        this.FindControl<TextBlock>("FleetNameChip")!.Text = prompt.FleetName;
        this.FindControl<TextBlock>("ActiveValue")!.Text = DescribeActive(prompt.ActivatedAt);
        this.FindControl<TextBlock>("CoupledValue")!.Text = DescribeCoupled(prompt);

        foreach (var option in BuildOptions(prompt.LeavableCharacterCount))
            Options.Add(option);
        foreach (var run in prompt.RunsInProgress)
            RunsInProgress.Add(run);

        // The completed count only ever shows when it is known (ET-185) — where it is not, this dialog reads exactly
        // as it did before that ticket: the running count alone, or nothing at all when nothing is running either.
        // Never a guess or an "unknown" filler standing in for the number.
        string? runsValueText = prompt.CompletedRunCount is { } completed
            ? RunsInProgress.Count > 0 ? $"{completed} completed · {RunsInProgress.Count} still running" : $"{completed} completed"
            : RunsInProgress.Count > 0 ? $"{RunsInProgress.Count} still running" : null;
        if (runsValueText is not null)
        {
            this.FindControl<Grid>("RunsRow")!.IsVisible = true;
            this.FindControl<TextBlock>("RunsValue")!.Text = runsValueText;
        }

        if (RunsInProgress.Count > 0)
        {
            var noun = RunsInProgress.Count == 1 ? "run" : "runs";
            this.FindControl<StackPanel>("RunsBlock")!.IsVisible = true;
            this.FindControl<TextBlock>("RunsBlockLabel")!.Text = $"THE {noun.ToUpperInvariant()} STILL GOING";
            this.FindControl<Border>("RunsKeepGoingChip")!.IsVisible = true;
            this.FindControl<TextBlock>("RunsKeepGoingText")!.Text =
                $"{RunsInProgress.Count} {noun} keep{(RunsInProgress.Count == 1 ? "s" : "")} going";
        }

        // Selected here and not in the XAML: the list is bound to Options, which this constructor fills AFTER
        // AvaloniaXamlLoader.Load has run — a SelectedIndex set on the still-empty list falls straight back to -1,
        // and a dialog that opens with nothing selected has a confirm button that does nothing.
        if (Options.Count > 0)
            this.FindControl<ListBox>("ExitList")!.SelectedIndex = 0;
        ApplyConfirmButton(Options.Count > 0 ? Options[0] : null);
    }

    /// <summary>
    /// The three ways out, safest first. Leaving is offered only when one of my own characters could actually leave:
    /// the fleet's owner is never a leave candidate (they hand the fleet over or disband it), so on a fleet where I
    /// fly nothing but the FC there is no such option to offer.
    /// </summary>
    private static IEnumerable<StopFleetOption> BuildOptions(int leavableCharacterCount)
    {
        yield return new StopFleetOption(
            StopFleetChoice.Stop,
            "STOP — back to standing by",
            "recommended",
            "The roster, the doctrine and the name all stay. Press START again next week and it runs again. Every character is free for another fleet straight away.",
            "STOP FLEET →",
            IsRecommended: true);

        yield return new StopFleetOption(
            StopFleetChoice.Conclude,
            "CONCLUDE — final",
            "cannot be undone",
            "The fleet drops to Concluded. It can no longer be started or joined, but it is kept for the record. For the night you are not going to repeat.",
            "CONCLUDE FLEET →",
            IsIrreversible: true);

        if (leavableCharacterCount > 0)
            yield return new StopFleetOption(
                StopFleetChoice.LeaveOnly,
                leavableCharacterCount == 1 ? "LEAVE — with my character" : "LEAVE — with one of my characters",
                "leaves the fleet alone",
                "The fleet runs on for everyone else; only that character is freed. This is what a member can do without being the FC.",
                "LEAVE FLEET →");
    }

    /// <summary>How long it has been running, and since when. Invariant on purpose: this is a clock readout, and
    /// the app's own tests run on a machine whose culture is not English (ET-34).</summary>
    private static string DescribeActive(DateTimeOffset? activatedAt)
    {
        if (activatedAt is not { } since)
            return "running";

        var elapsed = DateTimeOffset.UtcNow - since;
        if (elapsed < TimeSpan.Zero)
            elapsed = TimeSpan.Zero;

        return string.Create(CultureInfo.InvariantCulture,
            $"{elapsed:hh\\:mm\\:ss} · since {since.ToLocalTime():HH\\:mm}");
    }

    private static string DescribeCoupled(StopFleetPrompt prompt)
    {
        List<string> parts = [];
        if (prompt.OwnMemberCount > 0)
            parts.Add($"{prompt.OwnMemberCount} of your characters");
        if (prompt.OtherMemberCount > 0)
            parts.Add($"{prompt.OtherMemberCount} other pilot{(prompt.OtherMemberCount == 1 ? "" : "s")}");
        if (prompt.ExternalMemberCount > 0)
            parts.Add($"{prompt.ExternalMemberCount} external");

        return parts.Count == 0 ? "nobody on the roster" : string.Join(" + ", parts);
    }

    private void OnExitChanged(object? sender, SelectionChangedEventArgs e) =>
        ApplyConfirmButton((sender as ListBox)?.SelectedItem as StopFleetOption);

    /// <summary>
    /// The confirm button reads and weighs what the selection actually does: accent for the reversible stop, the
    /// destructive red for the terminal conclude. The class is swapped rather than a Foreground set locally — a
    /// local value beats a style setter, and the button would then keep whatever ink it was given first.
    /// </summary>
    private void ApplyConfirmButton(StopFleetOption? option)
    {
        if (this.FindControl<Button>("ConfirmButton") is not { } confirm)
            return;

        confirm.Content = option?.ConfirmLabel ?? "STOP FLEET →";
        confirm.Classes.Set("danger", option?.IsIrreversible == true);
        confirm.Classes.Set("accent", option?.IsIrreversible != true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(StopFleetChoice.Cancel);

    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        if (this.FindControl<ListBox>("ExitList")?.SelectedItem is not StopFleetOption chosen)
            return; // no selection — keep the dialog open rather than guess which exit was meant

        Close(chosen.Choice);
    }
}
