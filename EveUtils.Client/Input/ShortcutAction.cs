namespace EveUtils.Client.Input;

/// <summary>
/// Every keyboard shortcut EVE Together offers, keyed by what it does rather than by its key so Settings can list
/// and rebind them (ET-209). New entries here need a default in <see cref="KeyboardShortcutDefaults"/> and a
/// dispatch case wherever the action applies (<c>MainWindow</c> for the docked host, <c>ModuleHostService</c> for a
/// floating module window).
/// </summary>
public enum ShortcutAction
{
    /// <summary>Close the focused tab or floating module window — the exact same path as its own close button.</summary>
    CloseTab,

    /// <summary>Docked host only: select the next tab, wrapping past the last.</summary>
    NextTab,

    /// <summary>Docked host only: select the previous tab, wrapping past the first.</summary>
    PreviousTab,

    GoToTab1,
    GoToTab2,
    GoToTab3,
    GoToTab4,
    GoToTab5,
    GoToTab6,
    GoToTab7,
    GoToTab8,

    /// <summary>Docked host only: select the last tab (browser convention — not necessarily the 9th).</summary>
    GoToLastTab,

    /// <summary>Docked host only: reopen the most recently closed tab, for the modules simple enough to reopen from
    /// scratch (no per-entity context to reconstruct — see <c>MainWindowViewModel</c>).</summary>
    ReopenClosedTab,

    /// <summary>Re-read the current module, via <see cref="IRefreshableModule"/>.</summary>
    RefreshModule,

    /// <summary>Focus the current module's own search box, if it has one.</summary>
    FocusSearch,

    /// <summary>Open the Settings window. Docked host only (a floating Settings window makes this redundant there).</summary>
    OpenSettings,

    /// <summary>Cancel/close the focused modal dialog — never a tab, never the run window.</summary>
    CloseDialog,
}
