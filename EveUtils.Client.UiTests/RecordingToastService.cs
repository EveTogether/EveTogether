using System;
using System.Collections.Generic;
using EveUtils.Client.Notifications;

namespace EveUtils.Client.UiTests;

/// <summary>
/// <see cref="IToastService"/> double for headless UI tests: records every toast so a test can assert that an
/// action (e.g. joining a fleet) surfaced the expected confirmation. The real <see cref="ToastService"/> needs a
/// window's overlay layer, which headless tests don't have — this records instead of rendering.
/// </summary>
public sealed class RecordingToastService : IToastService
{
    /// <summary>Every toast shown, in order, as (title, message, kind) — action toasts included.</summary>
    public List<(string Title, string? Message, ToastKind Kind)> Toasts { get; } = new();

    /// <summary>The action toasts shown, in order, with their buttons — so a test can assert the offered choices.</summary>
    public List<(string Title, string? Message, ToastKind Kind, IReadOnlyList<ToastAction> Actions)> ActionToasts { get; } = new();

    public void Show(string title, string? message = null, ToastKind kind = ToastKind.Success, TimeSpan? expiration = null) =>
        Toasts.Add((title, message, kind));

    public void Show(string title, string? message, ToastKind kind, IReadOnlyList<ToastAction> actions)
    {
        Toasts.Add((title, message, kind));
        ActionToasts.Add((title, message, kind, actions));
    }
}
