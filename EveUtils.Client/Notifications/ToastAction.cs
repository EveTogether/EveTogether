using System;

namespace EveUtils.Client.Notifications;

/// <summary>
/// A labelled button on a toast. Clicking it runs <see cref="Run"/> and dismisses the toast — letting a toast carry a
/// choice (e.g. "open metrics" on a fleet-start toast, or Accept / Decline on a fleet invite) instead of being a
/// passive notification. <see cref="Style"/> tints the button (green/red) for affirmative/destructive choices.
/// Construct one per button and pass them to
/// <see cref="IToastService.Show(string, string?, ToastKind, System.Collections.Generic.IReadOnlyList{ToastAction})"/>.
/// </summary>
public sealed record ToastAction(string Label, Action Run, ToastActionStyle Style = ToastActionStyle.Default);
