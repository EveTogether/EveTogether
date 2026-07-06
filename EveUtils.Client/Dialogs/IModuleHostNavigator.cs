using System;
using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia.Controls;

namespace EveUtils.Client.Dialogs;

/// <summary>
/// One open feature module shown as a tab in the docked host. Wraps the module's content + title and a close
/// command. In floating mode the same module is a separate window instead.
/// </summary>
public sealed class HostTab
{
    public required Control Content { get; init; }
    public required string Title { get; init; }
    public required ICommand CloseCommand { get; init; }

    /// <summary>Rail group this module belongs to (fits/fleet/esi/inbox/logs), so the rail highlights the active
    /// tab's module. Null for modules with no rail entry (e.g. per-character metrics).</summary>
    public string? ModuleKey { get; init; }
}

/// <summary>
/// The main window's docked module host, as seen by the module-host service. The service owns the open module set +
/// lifecycle and drives this tab collection; the view-model exposes it for binding. In docked mode the host shows a
/// tab per open module (the home shows when there are none); in floating mode the modules are separate windows.
/// </summary>
public interface IModuleHostDisplay
{
    /// <summary>Floating mode is active → modules are separate windows; docked → they are tabs here.</summary>
    bool IsFloating { get; }

    /// <summary>The open module tabs (empty = the "home" landing is shown).</summary>
    ObservableCollection<HostTab> HostTabs { get; }

    /// <summary>The active tab (its content fills the host).</summary>
    HostTab? SelectedHostTab { get; set; }
}

/// <summary>
/// A feature window whose internal "Close" button must close its hosted tab when docked (the window itself is never
/// shown in that case). When floating, <see cref="CloseRequested"/> stays null and the button closes the real window.
/// </summary>
public interface IHostableModuleWindow
{
    Action? CloseRequested { get; set; }
}
