using System.Collections.Generic;
using Avalonia.Input;

namespace EveUtils.Client.Input;

/// <summary>
/// The out-of-the-box bindings (ET-209) — common browser/desktop conventions, deliberately excluding text-editing
/// keys (Ctrl+C/V/X/A/Z/Y, Ctrl+Backspace — those stay with text fields), anything with Ctrl+Alt (reserved
/// system-wide by the operator's own AutoHotkey script) and any global/app-quit shortcut. A user override in
/// Settings replaces an action's default entirely; see <see cref="KeyboardShortcutRegistry"/>.
/// </summary>
public static class KeyboardShortcutDefaults
{
    /// <summary>Refresh carries two conventional keys (F5 and the browser's Ctrl+R) — the only action with more
    /// than one default gesture. Every other action has exactly one.</summary>
    public static readonly IReadOnlyDictionary<ShortcutAction, IReadOnlyList<KeyGesture>> Gestures =
        new Dictionary<ShortcutAction, IReadOnlyList<KeyGesture>>
        {
            [ShortcutAction.CloseTab] = [new KeyGesture(Key.W, KeyModifiers.Control)],
            [ShortcutAction.NextTab] = [new KeyGesture(Key.Tab, KeyModifiers.Control)],
            [ShortcutAction.PreviousTab] = [new KeyGesture(Key.Tab, KeyModifiers.Control | KeyModifiers.Shift)],
            [ShortcutAction.GoToTab1] = [new KeyGesture(Key.D1, KeyModifiers.Control)],
            [ShortcutAction.GoToTab2] = [new KeyGesture(Key.D2, KeyModifiers.Control)],
            [ShortcutAction.GoToTab3] = [new KeyGesture(Key.D3, KeyModifiers.Control)],
            [ShortcutAction.GoToTab4] = [new KeyGesture(Key.D4, KeyModifiers.Control)],
            [ShortcutAction.GoToTab5] = [new KeyGesture(Key.D5, KeyModifiers.Control)],
            [ShortcutAction.GoToTab6] = [new KeyGesture(Key.D6, KeyModifiers.Control)],
            [ShortcutAction.GoToTab7] = [new KeyGesture(Key.D7, KeyModifiers.Control)],
            [ShortcutAction.GoToTab8] = [new KeyGesture(Key.D8, KeyModifiers.Control)],
            [ShortcutAction.GoToLastTab] = [new KeyGesture(Key.D9, KeyModifiers.Control)],
            [ShortcutAction.ReopenClosedTab] = [new KeyGesture(Key.T, KeyModifiers.Control | KeyModifiers.Shift)],
            [ShortcutAction.RefreshModule] = [new KeyGesture(Key.F5, KeyModifiers.None), new KeyGesture(Key.R, KeyModifiers.Control)],
            [ShortcutAction.FocusSearch] = [new KeyGesture(Key.F, KeyModifiers.Control)],
            [ShortcutAction.OpenSettings] = [new KeyGesture(Key.OemComma, KeyModifiers.Control)],
            [ShortcutAction.CloseDialog] = [new KeyGesture(Key.Escape, KeyModifiers.None)],
        };

    /// <summary>Label shown in the Settings list — what the action does, not its key.</summary>
    public static readonly IReadOnlyDictionary<ShortcutAction, string> DisplayNames = new Dictionary<ShortcutAction, string>
    {
        [ShortcutAction.CloseTab] = "Close current tab / window",
        [ShortcutAction.NextTab] = "Next tab",
        [ShortcutAction.PreviousTab] = "Previous tab",
        [ShortcutAction.GoToTab1] = "Go to tab 1",
        [ShortcutAction.GoToTab2] = "Go to tab 2",
        [ShortcutAction.GoToTab3] = "Go to tab 3",
        [ShortcutAction.GoToTab4] = "Go to tab 4",
        [ShortcutAction.GoToTab5] = "Go to tab 5",
        [ShortcutAction.GoToTab6] = "Go to tab 6",
        [ShortcutAction.GoToTab7] = "Go to tab 7",
        [ShortcutAction.GoToTab8] = "Go to tab 8",
        [ShortcutAction.GoToLastTab] = "Go to last tab",
        [ShortcutAction.ReopenClosedTab] = "Reopen last closed tab",
        [ShortcutAction.RefreshModule] = "Refresh current module",
        [ShortcutAction.FocusSearch] = "Focus the current module's search box",
        [ShortcutAction.OpenSettings] = "Open Settings",
        [ShortcutAction.CloseDialog] = "Cancel/close a dialog",
    };
}
