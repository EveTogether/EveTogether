using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Notifications;
using Avalonia.VisualTree;
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

    // Weak keys so a closed window's managers are collected with the window, and one manager per corner rather than
    // per window: a manager stacks every card it owns in a single panel, so corners sharing one would move each
    // other's toasts.
    private readonly ConditionalWeakTable<TopLevel, Dictionary<ToastPosition, WindowNotificationManager>> _managers = new();
    private readonly ConditionalWeakTable<WindowNotificationManager, Dictionary<string, object>> _replacements = new();
    private readonly Lock _replacementGate = new();
    private readonly Dictionary<string, long> _replacementVersions = new(StringComparer.Ordinal);
    private long _nextReplacementVersion;

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
        => Show(title, message, kind, actions, onClosed: null, replacementKey: null, position);

    public void Show(string title, string? message, ToastKind kind, IReadOnlyList<ToastAction> actions, Action? onClosed,
        string? replacementKey = null, ToastPosition? position = null)
    {
        if (actions.Count == 0)
        {
            Show(title, message, kind, expiration: null, position); // no buttons → a plain notification, with the default auto-dismiss
            return;
        }

        // Action toasts carry buttons, which Avalonia's Notification can't render, so they're shown as plain content
        // (ToastActionContent), and they never auto-dismiss — see ExpirationFor.
        var replacementVersion = _ReserveReplacement(replacementKey);
        ShowOnActiveWindow(position, manager => ShowAction(manager, title, message, kind, actions, onClosed, replacementKey,
            replacementVersion));
    }

    private void ShowAction(WindowNotificationManager manager, string title, string? message, ToastKind kind,
        IReadOnlyList<ToastAction> actions, Action? onClosed, string? replacementKey, long? replacementVersion)
    {
        if (!_IsCurrentReplacement(replacementKey, replacementVersion))
            return;

        var content = ToastActionContent.Build(title, message, kind, actions);
        var replacements = _replacements.GetValue(manager, _ => new Dictionary<string, object>(StringComparer.Ordinal));

        if (replacementKey is { } key && replacements.Remove(key, out var previous))
            manager.Close(previous);

        if (replacementKey is { } currentKey)
            replacements.Add(currentKey, content);

        manager.Show(content, ToNotificationType(kind), ExpirationFor(actions), null, () =>
        {
            if (replacementKey is { } key && replacements.TryGetValue(key, out var current)
                && ReferenceEquals(current, content))
                replacements.Remove(key);
            _ReleaseReplacement(replacementKey, replacementVersion);
            onClosed?.Invoke();
        }, []);
    }

    /// <summary>
    /// How long a card stays up: buttons make it a question, and a question that withdraws itself after five seconds
    /// is not a question, so an action toast stays until it is answered or dismissed.
    /// </summary>
    /// <remarks>
    /// Avalonia reads a null expiration as "use the default" (~5 s) rather than "never", which is what
    /// <see cref="TimeSpan.Zero"/> means — the two are easy to swap and the difference is invisible until a card
    /// vanishes while the user is reading it.
    /// </remarks>
    internal static TimeSpan? ExpirationFor(IReadOnlyList<ToastAction> actions) =>
        actions.Count > 0 ? TimeSpan.Zero : null;

    private long? _ReserveReplacement(string? replacementKey)
    {
        if (replacementKey is null)
            return null;

        lock (_replacementGate)
        {
            var version = ++_nextReplacementVersion;
            _replacementVersions[replacementKey] = version;
            return version;
        }
    }

    private bool _IsCurrentReplacement(string? replacementKey, long? replacementVersion)
    {
        if (replacementKey is null || replacementVersion is null)
            return true;

        lock (_replacementGate)
            return _replacementVersions.TryGetValue(replacementKey, out var current) && current == replacementVersion;
    }

    private void _ReleaseReplacement(string? replacementKey, long? replacementVersion)
    {
        if (replacementKey is null || replacementVersion is null)
            return;

        lock (_replacementGate)
        {
            if (_replacementVersions.TryGetValue(replacementKey, out var current) && current == replacementVersion)
                _replacementVersions.Remove(replacementKey);
        }
    }

    // Resolves the window the user is looking at and hands it the manager for the requested corner. A fresh manager
    // has not attached to the overlay layer yet and drops its very first Show, so that one is deferred a cycle.
    // Nothing open (headless tests / no desktop lifetime) → silent no-op.
    private void ShowOnActiveWindow(ToastPosition? requested, Action<WindowNotificationManager> show)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var host = ResolveActiveWindow();
            if (host is null)
                return;

            var (manager, isNew) = ManagerFor(host, requested);

            // Re-measured per toast rather than once: the titlebar scales with the theme and the status strip is
            // hidden in floating mode, so an inset taken at creation would go stale.
            manager.Margin = ChromeInset(host);

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

    /// <summary>
    /// How far a toast has to stay clear of the window's own chrome, so a card counted from the top or the bottom
    /// does not land on the titlebar or the status strip.
    /// </summary>
    internal static Thickness ChromeInset(TopLevel host) =>
        new(0, VisibleHeightOf(host, "TitleBar"), 0, VisibleHeightOf(host, "StatusBar"));

    private static double VisibleHeightOf(TopLevel host, string name) =>
        host.GetVisualDescendants().OfType<Control>()
            .FirstOrDefault(control => control.Name == name) is { IsVisible: true } found
            ? found.Bounds.Height
            : 0;

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
