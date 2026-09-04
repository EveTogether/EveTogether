namespace EveUtils.Client.Dialogs;

/// <summary>
/// One of the ways out offered by the stop dialog, as the list renders it (ET-166). The three sit side by side on
/// purpose — that they read as different acts is the point of the screen, not decoration — so each carries its own
/// consequence note: <see cref="IsRecommended"/> paints it as the safe default, <see cref="IsIrreversible"/> as the
/// one there is no way back from, and neither as "this leaves the fleet alone".
/// </summary>
/// <param name="Choice">What confirming this option does.</param>
/// <param name="Title">The verb, spelled out with its destination.</param>
/// <param name="Note">The short consequence chip beside the title.</param>
/// <param name="Hint">What it means for the roster, in the FC's terms.</param>
/// <param name="ConfirmLabel">What the confirm button reads while this option is selected.</param>
public sealed record StopFleetOption(
    StopFleetChoice Choice,
    string Title,
    string Note,
    string Hint,
    string ConfirmLabel,
    bool IsRecommended = false,
    bool IsIrreversible = false)
{
    /// <summary>Neither the safe default nor the terminal one — the option that leaves the fleet running. Its own
    /// property rather than a negation in the view: a chip that is both .warn and .dim takes whichever style rule
    /// the theme declares last, and that is not a decision the view should be making by accident.</summary>
    public bool IsNeutral => !IsRecommended && !IsIrreversible;
}
