using System;
using System.Collections.Generic;
using System.Linq;
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
        public bool Detached;     // popped out of the tab strip on its own, independent of dock mode (ET-111)
        public HostTab? Tab;      // docked tab wrapper
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
    public int FloatingWindowCount => _modules.Count(m => m.Shown);

    /// <summary>Close every floating module window (used when the main window is closing).</summary>
    public void CloseFloatingWindows()
    {
        foreach (var module in _modules.Where(m => m.Shown).ToList())
            module.Window.Close(); // fires Closed → OnWindowClosed drops it from the set
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
        {
            hostable.CloseRequested = () => Dismiss(frame);
            hostable.DockRequested = () => Dock(frame);
        }
        window.Closed += (_, _) => OnWindowClosed(frame);
        // Only ever reaches this window while it is actually shown — i.e. floating (docked hides it and steals its
        // content, so it cannot hold keyboard focus then). The docked case is MainWindow's own key handling instead.
        window.KeyDown += (_, e) => _OnFloatingWindowKeyDown(e, frame);

        _modules.Add(frame);
        Render(select: frame);
        return window.DataContext;
    }

    /// <summary>Re-render after a dock/float switch — migrates the open modules to the other mode (no orphans).
    /// Switching to docked is "all modules", per the rail button's own tooltip: any tab popped out on its own
    /// rejoins the strip along with everything else rather than staying stranded as a window (ET-111).</summary>
    public void SwitchMode()
    {
        if (_host is not null && !_host.IsFloating)
            foreach (var m in _modules) m.Detached = false;
        Render(select: null);
    }

    /// <summary>Pops one open tab into its own floating window, leaving the dock mode and every other module
    /// untouched (ET-111) — the host's top-right button, distinct from <see cref="SwitchMode"/>.</summary>
    public void PopOut(HostTab tab)
    {
        var frame = _modules.FirstOrDefault(m => ReferenceEquals(m.Tab, tab));
        if (frame is null) return;
        frame.Detached = true;
        Render(select: frame);
    }

    /// <summary>Docks one detached frame back into a tab — reached through the window's own DOCK button, wired
    /// in <see cref="Open"/> (ET-111).</summary>
    private void Dock(ModuleFrame frame)
    {
        frame.Detached = false;
        Render(select: frame);
    }

    private void Render(ModuleFrame? select)
    {
        if (_host is null) return;

        bool IsWindowed(ModuleFrame m) => _host.IsFloating || m.Detached;
        var windowed = _modules.Where(IsWindowed).ToList();
        var tabbed = _modules.Where(m => !IsWindowed(m)).ToList();

        // Release every module from the docked tab strip first, so a module moving into a window is not still
        // visually parented by the ContentPresenter its old tab bound to (Avalonia refuses a second parent).
        _host.SelectedHostTab = null;
        _host.HostTabs.Clear();

        foreach (var m in windowed)
        {
            if (!ReferenceEquals(m.Window.Content, m.Content)) m.Window.Content = m.Content;
            // The chrome's own DOCK button only makes sense for a frame popped out on its own while the app itself
            // stays docked — with the app floating, the rail switch is already the way back (ET-111).
            if (m.Window is ChromedWindow chromed) chromed.ShowDockButton = m.Detached && !_host.IsFloating;
            // Shown ownerless (not Show(_owner)) so a floating module is independent of the main window: minimizing
            // the main window no longer minimizes it. The main window's close handler closes these explicitly.
            if (!m.Shown) { m.Window.Show(); m.Shown = true; }
        }
        foreach (var m in tabbed)
        {
            if (m.Shown) { m.Window.Hide(); m.Shown = false; }
            if (ReferenceEquals(m.Window.Content, m.Content)) m.Window.Content = null;   // steal for the tab
        }

        foreach (var m in tabbed) _host.HostTabs.Add(m.Tab!);

        // Docked, "select" means the tab the host switches to; windowed there are no tabs, so the same intent has
        // to be spoken as raising the window. Without this, asking for a module that is already open does nothing
        // visible when it happens to sit behind the one you asked from — which is exactly the case ET-171's back
        // buttons create: FLEETS from a fleet screen, with the overview already open behind it.
        if (select is not null && windowed.Contains(select))
            select.Window.Activate();
        else
            _host.SelectedHostTab = (select is not null && tabbed.Contains(select) ? select : tabbed.LastOrDefault())?.Tab;
    }

    private void Dismiss(ModuleFrame frame)
    {
        var index = _modules.IndexOf(frame);
        var removed = _modules.Remove(frame);
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
            default:
                return;
        }
        e.Handled = true;
    }
}
