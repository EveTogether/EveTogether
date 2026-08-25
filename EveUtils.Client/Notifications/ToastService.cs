using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Notifications;
using Avalonia.Threading;
using EveUtils.Shared.DependencyInjection;

namespace EveUtils.Client.Notifications;

/// <summary>
/// <see cref="IToastService"/> backed by Avalonia's <see cref="WindowNotificationManager"/>. A toast is shown on the
/// window the user is actually looking at: in floating mode feature views like Fleets open as their own
/// windows, so a fixed main-window manager would surface confirmations on the wrong window. Show() resolves the
/// active window each time and keeps one manager per window and corner (created lazily — the window's overlay layer
/// exists by then because the user is interacting with it). In headless tests there is no desktop lifetime → silent
/// no-op. Singleton so the per-window managers + the configured <see cref="Position"/> are shared app-wide.
/// </summary>
public sealed class ToastService : IToastService, ISingletonService
{
    /// <summary>Setting key for the in-window corner toasts appear in (persisted enum name, default TopRight).</summary>
    public const string PositionSettingKey = "toasts.position";

    // Weak keys so a closed window's managers are collected with the window. One manager per corner, not per window:
    // a manager stacks all its cards in a single panel, so sharing one across corners would drag a toast that is still
    // standing (an update offer waits for a click) along to wherever the next toast asked to appear.
    private readonly ConditionalWeakTable<TopLevel, Dictionary<ToastPosition, WindowNotificationManager>> _managers = new();

    /// <summary>Where toasts appear within the window. Settable live (Settings); applied on the next toast.</summary>
    public ToastPosition Position { get; set; } = ToastPosition.TopRight;

    /// <summary>Parses a persisted setting value into <see cref="Position"/> (unknown/null → TopRight).</summary>
    public void ApplyPositionSetting(string? value) =>
        Position = Enum.TryParse<ToastPosition>(value, ignoreCase: true, out var parsed) ? parsed : ToastPosition.TopRight;

    public void Show(string title, string? message = null, ToastKind kind = ToastKind.Success, TimeSpan? expiration = null,
        ToastPosition? position = null) =>
        ShowOnActiveWindow(position, manager => manager.Show(new Notification(title, message, ToNotificationType(kind), expiration)));

    public void Show(string title, string? message, ToastKind kind, IReadOnlyList<ToastAction> actions,
        ToastPosition? position = null)
    {
        if (actions.Count == 0)
        {
            Show(title, message, kind, expiration: null, position); // no buttons → a plain notification, with the default auto-dismiss
            return;
        }

        // Action toasts carry buttons, which Avalonia's Notification can't render, so they're shown as plain content
        // (ToastActionContent). They have no expiration: the card persists until the user picks an action or closes it,
        // so the choice isn't missed.
        ShowOnActiveWindow(position, manager => manager.Show(ToastActionContent.Build(title, message, kind, actions)));
    }

    // Resolves the window the user is looking at, lazily keeps one notification manager per window and corner, and
    // hands the right one to <paramref name="show"/>. A freshly created manager hasn't been laid out yet, so its very
    // first Show is dropped (the symptom was "first Join click shows no toast, the second does"): defer the first toast
    // one cycle so the manager attaches to the overlay layer; later toasts in the same corner show immediately.
    // Nothing open (headless tests / no desktop lifetime) → silent no-op.
    private void ShowOnActiveWindow(ToastPosition? requested, Action<WindowNotificationManager> show)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var host = ResolveActiveWindow();
            if (host is null)
                return;

            var (manager, isNew) = ManagerFor(host, requested);

            if (isNew)
                Dispatcher.UIThread.Post(() => show(manager), DispatcherPriority.Background);
            else
                show(manager);
        });
    }

    /// <summary>
    /// The manager for one window and corner, created on first use. <c>IsNew</c> says it was just created, which is
    /// what the deferred first Show above needs to know.
    /// </summary>
    internal (WindowNotificationManager Manager, bool IsNew) ManagerFor(TopLevel host, ToastPosition? requested)
    {
        // Read the setting here rather than at the call site, so a live change applies to the next toast.
        var corner = requested ?? Position;
        var byCorner = _managers.GetValue(host, _ => new Dictionary<ToastPosition, WindowNotificationManager>());

        if (byCorner.TryGetValue(corner, out var existing))
            return (existing, false);

        var created = new WindowNotificationManager(host) { MaxItems = 3, Position = ToNotificationPosition(corner) };
        byCorner.Add(corner, created);

        return (created, true);
    }

    private static TopLevel? ResolveActiveWindow()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return null;
        return desktop.Windows.FirstOrDefault(w => w.IsActive) ?? desktop.MainWindow;
    }

    private static NotificationPosition ToNotificationPosition(ToastPosition position) => position switch
    {
        ToastPosition.TopLeft => NotificationPosition.TopLeft,
        ToastPosition.TopCenter => NotificationPosition.TopCenter,
        ToastPosition.BottomLeft => NotificationPosition.BottomLeft,
        ToastPosition.BottomCenter => NotificationPosition.BottomCenter,
        ToastPosition.BottomRight => NotificationPosition.BottomRight,
        _ => NotificationPosition.TopRight,
    };

    private static NotificationType ToNotificationType(ToastKind kind) => kind switch
    {
        ToastKind.Information => NotificationType.Information,
        ToastKind.Warning => NotificationType.Warning,
        ToastKind.Error => NotificationType.Error,
        _ => NotificationType.Success,
    };
}
