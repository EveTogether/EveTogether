using System.Linq;
using Avalonia.Controls;
using Avalonia.VisualTree;
using EveUtils.Client.Dialogs;

namespace EveUtils.Client.Input;

/// <summary>
/// The two shortcut actions that apply the same way in a docked tab and a floating module window, so both
/// <c>MainWindow</c> and <c>ModuleHostService</c> call the same code instead of each reimplementing it.
/// </summary>
public static class ShortcutDispatch
{
    /// <summary>The well-known name a module's own search box opts into to be reachable from Ctrl+F. A module with
    /// no search box (most of them) is simply not found — this is a no-op there, not an error.</summary>
    public const string SearchBoxName = "ModuleSearchBox";

    public static void RefreshModule(Control? content)
    {
        if (content?.DataContext is IRefreshableModule refreshable)
            refreshable.RefreshModule();
    }

    public static void FocusSearch(Control? content)
    {
        var box = content?.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.Name == SearchBoxName);
        box?.Focus();
    }
}
