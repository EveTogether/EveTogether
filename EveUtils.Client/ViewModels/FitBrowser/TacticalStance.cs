namespace EveUtils.Client.ViewModels.FitBrowser;

/// <summary>The three Tactical Destroyer stances. Drives the in-game-style glyph in the mode selector: every stance
/// shares an upward chevron, framed differently — Defense sits in a ring, Sharpshooter adds crosshair ticks (the
/// targeting reticle), Propulsion is a bare chevron.</summary>
public enum TacticalStance
{
    Unknown,
    Defense,
    Sharpshooter,
    Propulsion
}
