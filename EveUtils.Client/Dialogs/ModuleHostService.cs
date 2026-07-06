using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.Input;

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
        public string? Id;        // stable de-dupe identity (e.g. one per fleet); falls back to Title when null
        public bool Shown;        // window currently shown (floating)
        public HostTab? Tab;      // docked tab wrapper
    }

    private Window? _owner;
    private IModuleHostDisplay? _host;
    private readonly List<ModuleFrame> _modules = new();

    public void SetOwner(Window owner) => _owner = owner;
    public void SetHost(IModuleHostDisplay host) => _host = host;

    /// <summary>Number of modules currently shown as their own (floating) windows — i.e. pop-outs. Docked modules are
    /// tabs inside the main window and are not counted (closing the main window takes them with it).</summary>
    public int FloatingWindowCount => _modules.Count(m => m.Shown);

    /// <summary>Close every floating module window (used when the main window is closing).</summary>
    public void CloseFloatingWindows()
    {
        foreach (var module in _modules.Where(m => m.Shown).ToList())
            module.Window.Close(); // fires Closed → OnWindowClosed drops it from the set
    }

    /// <summary>Open a feature window as a module. Re-opening the same module just re-selects it; identity is
    /// <paramref name="moduleId"/> when given (so several instances of one window type — e.g. one roster per fleet —
    /// stay distinct even when their titles match), otherwise the title. <paramref name="moduleKey"/> tags the rail
    /// group so the rail can highlight the active tab's module.</summary>
    public void Open(Window window, string title, string? moduleKey = null, string? moduleId = null)
    {
        if (_owner is null) return;
        if (_host is null) { window.Show(_owner); return; }   // no host wired (e.g. some tests)

        var existing = _modules.FirstOrDefault(m => moduleId is not null ? m.Id == moduleId : m.Title == title);
        if (existing is not null) { Render(select: existing); return; }

        var content = window.Content as Control;
        if (content is null) return;

        // Pin the content to its module VM + carry the window's NameScope so all bindings (plain {Binding} and
        // root-name {Binding #FleetsRoot...}) keep resolving wherever the content is parented.
        content.DataContext = window.DataContext;
        var scope = NameScope.GetNameScope(window);
        if (scope is not null && NameScope.GetNameScope(content) is null)
            NameScope.SetNameScope(content, scope);

        var frame = new ModuleFrame { Window = window, Content = content, Title = title, Id = moduleId };
        frame.Tab = new HostTab { Content = content, Title = title, ModuleKey = moduleKey, CloseCommand = new RelayCommand(() => Dismiss(frame)) };
        if (window is IHostableModuleWindow hostable)
            hostable.CloseRequested = () => Dismiss(frame);
        window.Closed += (_, _) => OnWindowClosed(frame);

        _modules.Add(frame);
        Render(select: frame);
    }

    /// <summary>Re-render after a dock/float switch — migrates the open modules to the other mode (no orphans).</summary>
    public void SwitchMode() => Render(select: null);

    private void Render(ModuleFrame? select)
    {
        if (_host is null) return;

        if (_host.IsFloating)
        {
            // Release any hosted content from the tabs first, then hand it back to each window and show them.
            _host.SelectedHostTab = null;
            _host.HostTabs.Clear();
            foreach (var m in _modules)
            {
                if (!ReferenceEquals(m.Window.Content, m.Content)) m.Window.Content = m.Content;
                // Shown ownerless (not Show(_owner)) so a floating module is independent of the main window: minimizing
                // the main window no longer minimizes it. The main window's close handler closes these explicitly.
                if (!m.Shown) { m.Window.Show(); m.Shown = true; }
            }
        }
        else
        {
            foreach (var m in _modules)
            {
                if (m.Shown) { m.Window.Hide(); m.Shown = false; }
                if (ReferenceEquals(m.Window.Content, m.Content)) m.Window.Content = null;   // steal for the tab
            }
            _host.HostTabs.Clear();
            foreach (var m in _modules) _host.HostTabs.Add(m.Tab!);
            _host.SelectedHostTab = (select ?? _modules.LastOrDefault())?.Tab;
        }
    }

    private void Dismiss(ModuleFrame frame)
    {
        var index = _modules.IndexOf(frame);
        var removed = _modules.Remove(frame);
        frame.Window.Close();   // fires Closed → the window's own cleanup (e.g. EsiMetrics disposes its VM)
        if (removed)
        {
            var neighbour = _modules.Count == 0 ? null : _modules[System.Math.Min(index, _modules.Count - 1)];
            Render(select: neighbour);
        }
    }

    // A floating module window closed by the user (its X) — drop it from the set and re-render.
    private void OnWindowClosed(ModuleFrame frame)
    {
        if (_modules.Remove(frame)) Render(select: null);
    }
}
