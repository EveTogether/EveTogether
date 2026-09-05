namespace EveUtils.Client.ViewModels.Fleets;

/// <summary>
/// What each action costs on a wide fleet row: the rendered button's own width plus the 3 px margin between two
/// picks, measured off the real buttons at 1578 px rather than estimated.
///
/// These are numbers and not a layout pass because of the order the row has to decide things in. JOIN belongs on the
/// row when it fits (Jithran, 2026-09-04: <i>"als het past zou de join in de rij het beste zijn"</i>), and when it
/// does not fit it belongs in the "⋯" menu — so the row must know whether the button fits <i>before</i> it draws it,
/// and before it knows what goes in the menu. A <c>WrapPanel</c> only ever answers "does this fit" by wrapping, and
/// a row breaking to a second line is the one answer this row may not give.
///
/// <c>FleetRowActionWidthTests</c> renders the buttons and holds every constant here against what they measure, so a
/// change to the font, the padding or a label fails a test instead of quietly breaking a row.
/// </summary>
public static class FleetRowActionWidths
{
    public const double Stop = 45;
    public const double Start = 49;
    public const double Join = 42;
    public const double Request = 63;
    public const double Manage = 60;
    public const double View = 43;
    public const double Metrics = 61;
    public const double Share = 51;
    public const double Leave = 48;
    /// <summary>SIGN OFF on a Forming fleet row (ET-169) — a member's own act, so it can stand beside LEAVE on the
    /// same row rather than only ever replacing it.</summary>
    public const double SignOff = 63;
    /// <summary>DELETE on a finished row. The same command reads DISBAND behind the "⋯" of a live fleet, where that
    /// is the right verb; there is nothing left to disband about a fleet that is already over (scherm 1: VERWIJDER).</summary>
    public const double Delete = 55;

    /// <summary>RUNS on a finished row (ET-185, scherm 1) — opens the runs overview filtered to this fleet. Unlike
    /// DELETE it needs no ownership, so it can stand on any finished row this client can see.</summary>
    public const double Runs = 46;

    /// <summary>The "⋯" button. It only stands when something is actually folded — so a row that puts its last
    /// action back on the bar gets these 31 px back, which is sometimes exactly what makes that action fit.</summary>
    public const double Overflow = 31;
}
