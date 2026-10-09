using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Input;
using EveUtils.Client.Views;
using Material.Icons;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.Dialogs;

/// <summary>
/// Owns the open feature-module set and renders it per mode: docked = a tab per module in the main window's
/// host (one bron van waarheid), floating = a separate window per module. A dock/float switch migrates the open set
/// between the two; closing a tab/window disposes the module's view-model via the window's own Closed handler.
/// Extracted from DialogService so the dialog service stays a thin façade.
/// </summary>
public sealed class ModuleHostService
{
    private sealed class ModuleFrame
    {
        public required Window Window;
        public required Control Content;
        public required string Title;
        public required string Id;
        public bool Shown;        // window currently shown (floating)
        public HostTab? Tab;      // docked tab wrapper
        public PoppedModuleWindow? Popout;   // the window the content lives in while popped out (ET-396)
        public HostTab? PlaceholderTab;      // what the tab shows meanwhile

        public HostTab? TabShown => Popout is null ? Tab : PlaceholderTab;
    }

    private Window? _owner;
    private IModuleHostDisplay? _host;
    private readonly List<ModuleFrame> _modules = new();

    public void SetOwner(Window owner) => _owner = owner;
    public void SetHost(IModuleHostDisplay host) => _host = host;

    /// <summary>Raised with a module's id whenever it closes, docked or floating (ET-209's "reopen last closed
    /// tab" listens here — only for the handful of no-argument modules simple enough to reopen from scratch; see
    /// <c>MainWindowViewModel</c>).</summary>
    public event Action<string>? ModuleClosed;

    /// <summary>Number of modules currently shown as their own (floating) windows — i.e. pop-outs. Docked modules are
    /// tabs inside the main window and are not counted (closing the main window takes them with it).</summary>
    public int FloatingWindowCount => _modules.Count(m => m.Shown || m.Popout is not null);

    /// <summary>Raised when a module was popped out, put back, or the dock/float switch changed whether one can be.</summary>
    public event Action? PopOutStateChanged;

    /// <summary>A popped-out module needs the docked host: floating, every module already is a window.</summary>
    public bool CanPopOut => _host is { IsFloating: false };

    /// <summary>The window a popped-out module lives in, for tests to look into.</summary>
    internal Window? PopoutOf(string moduleId) => _modules.FirstOrDefault(m => m.Id == moduleId)?.Popout;

    /// <summary>The window of its own the module with this view model is in right now — floating or popped out — or
    /// null for a docked module, whose window is the main one.</summary>
    internal Window? WindowShowing(object viewModel) =>
        _modules.FirstOrDefault(m => ReferenceEquals(m.Content.DataContext, viewModel)) is { } frame
            ? frame.Popout ?? (frame.Shown ? frame.Window : null)
            : null;

    public bool IsPoppedOut(string moduleId) => _modules.Any(m => m.Id == moduleId && m.Popout is not null);

    /// <summary>The id of the docked module whose own tab this is (ET-111), or null for a placeholder, no tab, or
    /// floating mode.</summary>
    public string? PoppableModuleId(HostTab? tab) =>
        tab is null || !CanPopOut ? null : _modules.FirstOrDefault(m => m.Popout is null && ReferenceEquals(m.Tab, tab))?.Id;

    /// <summary>Close every floating module window (used when the main window is closing).</summary>
    public void CloseFloatingWindows()
    {
        foreach (var module in _modules.Where(m => m.Shown).ToList())
            module.Window.Close(); // fires Closed → OnWindowClosed drops it from the set
        foreach (var module in _modules.Where(m => m.Popout is not null).ToList())
            _ReleasePopout(module)?.Close();
    }

    /// <summary>Open a feature window as a module. Re-opening the same module id re-selects it. <paramref
    /// name="moduleKey"/> tags the rail group so the rail can highlight the active tab's module; <paramref
    /// name="icon"/> is the symbol its tab carries (ET-171).
    ///
    /// The icon defaults for the sake of the tests that drive this service straight, which open a module to check
    /// what the host does with it and have no opinion about its tab's symbol. <c>DialogService.Route</c>, the one
    /// production caller, passes it for every screen.</summary>
    /// <returns>The view-model of the module now on screen: the caller's own, or the one already standing under this
    /// id — which is what a caller has to talk to after re-opening, since its own was just dropped.</returns>
    public object? Open(Window window, string title, string? moduleKey, string moduleId,
        MaterialIconKind icon = MaterialIconKind.Application)
    {
        if (_owner is null) return null;
        if (_host is null) { window.Show(_owner); return window.DataContext; }   // no host wired (e.g. some tests)

        var existing = _modules.FirstOrDefault(m => m.Id == moduleId);
        if (existing is not null)
        {
            // The module is already open, so the caller's freshly built window and view-model are surplus. Give the
            // standing module the chance to re-read what it snapshotted when it was built — otherwise re-opening a
            // screen silently shows the state it had when it first opened (ET-46) — and dispose the duplicate we are
            // dropping, which would otherwise keep its bus subscriptions and render registrations alive unseen.
            (existing.Content.DataContext as IRefreshableModule)?.RefreshModule();
            if (!ReferenceEquals(window.DataContext, existing.Content.DataContext))
                (window.DataContext as System.IDisposable)?.Dispose();
            Render(select: existing);
            return existing.Content.DataContext;
        }

        var content = window.Content as Control;
        if (content is null) return null;

        // Pin the content to its module VM + carry the window's NameScope so all bindings (plain {Binding} and
        // root-name {Binding #FleetsRoot...}) keep resolving wherever the content is parented.
        content.DataContext = window.DataContext;
        var scope = NameScope.GetNameScope(window);
        if (scope is not null && NameScope.GetNameScope(content) is null)
            NameScope.SetNameScope(content, scope);

        var frame = new ModuleFrame { Window = window, Content = content, Title = title, Id = moduleId };
        frame.Tab = new HostTab { Content = content, Title = title, ModuleKey = moduleKey, Icon = icon, CloseCommand = new RelayCommand(() => Dismiss(frame)) };
        if (window is IHostableModuleWindow hostable)
            hostable.CloseRequested = () => Dismiss(frame);
        window.Closed += (_, _) => OnWindowClosed(frame);
        // Only ever reaches this window while it is actually shown — i.e. floating (docked hides it and steals its
        // content, so it cannot hold keyboard focus then). The docked case is MainWindow's own key handling instead.
        window.KeyDown += (_, e) => _OnFloatingWindowKeyDown(e, frame);

        _modules.Add(frame);
        Render(select: frame);
        return window.DataContext;
    }

    /// <summary>Re-render after a dock/float switch — migrates the open modules to the other mode (no orphans).</summary>
    public void SwitchMode()
    {
        Render(select: null);
        PopOutStateChanged?.Invoke();
    }

    /// <summary>
    /// Moves a docked module's content into a window of its own (ET-396). The same view and view model move — nothing
    /// is rebuilt — and the tab keeps its place with <paramref name="placeholderFor"/>'s control in it. Independent of
    /// the global DOCK/FLOAT: only this module leaves the main window.
    /// </summary>
    public void PopOut(string moduleId, Func<Control, Control> placeholderFor, string geometryKey)
    {
        ModuleFrame? frame = _modules.FirstOrDefault(m => m.Id == moduleId);
        if (_host is null || _host.IsFloating || frame?.Tab is not { } tab) return;
        if (frame.Popout is not null)
        {
            _Raise(frame.Popout);
            return;
        }

        // A control has one parent: the tab strip lets go of the content before the window takes it.
        _host.SelectedHostTab = null;
        _host.HostTabs.Clear();
        _FlushLayout(_owner);

        var popout = new PoppedModuleWindow(frame.Window.Title ?? frame.Title, geometryKey, frame.Content,
            new Size(frame.Window.Width, frame.Window.Height), new Size(frame.Window.MinWidth, frame.Window.MinHeight));
        frame.Popout = popout;
        frame.PlaceholderTab = new HostTab
        {
            Content = placeholderFor(frame.Content),
            Title = tab.Title,
            ModuleKey = tab.ModuleKey,
            Icon = tab.Icon,
            CloseCommand = tab.CloseCommand
        };
        popout.Closed += (_, _) => _OnPopoutClosed(frame, popout);
        popout.KeyDown += (_, e) => _OnFloatingWindowKeyDown(e, frame);
        popout.Show();   // ownerless, like a floating module: minimizing the main window must not take it along

        Render(select: frame);
        PopOutStateChanged?.Invoke();
    }

    /// <summary>Puts a popped-out module back into its tab and closes its window.</summary>
    public void PutBack(string moduleId)
    {
        ModuleFrame? frame = _modules.FirstOrDefault(m => m.Id == moduleId);
        if (frame is null) return;
        _ReleasePopout(frame)?.Close();
        Render(select: frame);
        PopOutStateChanged?.Invoke();
    }

    /// <summary>Brings a popped-out module's window forward.</summary>
    public void FocusPopout(string moduleId)
    {
        if (_modules.FirstOrDefault(m => m.Id == moduleId)?.Popout is { } popout)
            _Raise(popout);
    }

    // Detaches the content from the window and forgets the popped-out state; the caller closes the window when it is
    // still open, and re-renders.
    private static PoppedModuleWindow? _ReleasePopout(ModuleFrame frame)
    {
        if (frame.Popout is not { } popout) return null;
        frame.Popout = null;
        frame.PlaceholderTab = null;
        popout.Content = null;
        _FlushLayout(popout);
        return popout;
    }

    // A control that changes windows must not still be waiting in the old window's layout queue: that manager would arrange
    // it under the new one and throw. Running the old window's pass now, after the control left it, empties that queue.
    private static void _FlushLayout(Window? window) => window?.UpdateLayout();

    // The window's own X: same as PUT BACK IN MAIN WINDOW.
    private void _OnPopoutClosed(ModuleFrame frame, PoppedModuleWindow popout)
    {
        if (!ReferenceEquals(frame.Popout, popout)) return;   // already put back, dismissed or migrated
        _ReleasePopout(frame);
        PopOutStateChanged?.Invoke();
    }

    private static void _Raise(Window window)
    {
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Activate();
    }

    private void Render(ModuleFrame? select)
    {
        if (_host is null) return;

        if (_host.IsFloating)
        {
            // Release any hosted content from the tabs first, then hand it back to each window and show them. A
            // popped-out module goes back to its own window like the rest: floating, every module is one.
            _host.SelectedHostTab = null;
            _host.HostTabs.Clear();
            foreach (var popped in _modules.Where(m => m.Popout is not null).ToList())
                _ReleasePopout(popped)?.Close();
            foreach (var m in _modules)
            {
                if (!ReferenceEquals(m.Window.Content, m.Content)) m.Window.Content = m.Content;
                // Shown ownerless (not Show(_owner)) so a floating module is independent of the main window: minimizing
                // the main window no longer minimizes it. The main window's close handler closes these explicitly.
                if (!m.Shown) { m.Window.Show(); m.Shown = true; }
            }

            // Docked, "select" means the tab the host switches to; floating there are no tabs, so the same intent has
            // to be spoken as raising the window. Without this, asking for a module that is already open does nothing
            // visible when it happens to sit behind the one you asked from — which is exactly the case ET-171's back
            // buttons create: FLEETS from a fleet screen, with the overview already open behind it.
            select?.Window.Activate();
        }
        else
        {
            foreach (var m in _modules)
            {
                if (m.Shown) { m.Window.Hide(); m.Shown = false; }
                if (m.Popout is null && ReferenceEquals(m.Window.Content, m.Content)) m.Window.Content = null;   // steal for the tab
            }
            _host.HostTabs.Clear();
            foreach (var shown in _modules.Select(m => m.TabShown).OfType<HostTab>()) _host.HostTabs.Add(shown);
            _host.SelectedHostTab = (select ?? _modules.LastOrDefault())?.TabShown;

            // Asking for a popped-out module (rail, shortcut, a fleet's MAP action) raises its window: the tab is only a placeholder.
            if (select?.Popout is { } popout) _Raise(popout);
        }
    }

    private void Dismiss(ModuleFrame frame)
    {
        var index = _modules.IndexOf(frame);
        var removed = _modules.Remove(frame);
        _ReleasePopout(frame)?.Close();
        frame.Window.Close();   // fires Closed → the window's own cleanup (e.g. EsiMetrics disposes its VM)
        if (removed)
        {
            ModuleClosed?.Invoke(frame.Id);
            var neighbour = _modules.Count == 0 ? null : _modules[System.Math.Min(index, _modules.Count - 1)];
            Render(select: neighbour);
        }
    }

    // A floating module window closed by the user (its X) — drop it from the set and re-render.
    private void OnWindowClosed(ModuleFrame frame)
    {
        if (_modules.Remove(frame))
        {
            ModuleClosed?.Invoke(frame.Id);
            Render(select: null);
        }
    }

    // Floating-only shortcuts (ET-209): Close mirrors the chrome's own titlebar X exactly (Window.Close(), not
    // Dismiss — Dismiss is for the tab's X, which never applies here since a floating window carries no tab).
    // Tab-cycling/reopen/settings are meaningless without a tab strip, so they are MainWindow's own handling only.
    private void _OnFloatingWindowKeyDown(KeyEventArgs e, ModuleFrame frame)
    {
        if (e.Handled) return;
        var registry = Program.Services?.GetService<KeyboardShortcutRegistry>();
        if (registry is null || !registry.TryResolve(new KeyGesture(e.Key, e.KeyModifiers), out var action)) return;

        switch (action)
        {
            case ShortcutAction.CloseTab:
                frame.Window.Close();
                break;
            case ShortcutAction.RefreshModule:
                ShortcutDispatch.RefreshModule(frame.Content);
                break;
            case ShortcutAction.FocusSearch:
                ShortcutDispatch.FocusSearch(frame.Content);
                break;
            case ShortcutAction.ToggleOpsec:
                ShortcutDispatch.ToggleOpsec();
                break;
            default:
                return;
        }
        e.Handled = true;
    }
}
