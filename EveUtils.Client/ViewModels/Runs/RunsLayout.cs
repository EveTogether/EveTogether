namespace EveUtils.Client.ViewModels.Runs;

/// <summary>
/// The one width the runs screen changes shape at (ET-291). Read off the bounds of the module content — the docked
/// tab's column or the floating window — never off the app window, since <c>ModuleHostService</c> moves the very same
/// content between the two.
/// </summary>
public static class RunsLayout
{
    /// <summary>At and above this, the selected run reads in a pane beside the list; below it, in a drawer over it.
    /// Derived from the band RO-3 and RO-4 put above the list: a 400 px strip plus two filter blocks of two ~178 px
    /// tiles, their inner spacing and the gaps between them, is about 1204. Below that the list beside a 400 px pane
    /// is cramped as well. The two widths this screen is actually used at — 758 floating and 1303 docked — sit well
    /// clear on either side.</summary>
    public const double WideFrom = 1200;

    /// <summary>The pane beside the list, and the drawer's own width over it.</summary>
    public const double PaneWidth = 400;

    public const double DrawerWidth = 430;

    /// <summary>What the drawer leaves of the list behind it at the narrowest widths: enough to see that the list is
    /// still there and to click it closed.</summary>
    public const double DrawerMinimumGap = 64;
}
