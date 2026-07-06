namespace EveUtils.Shared.Modules.Gamelog.Models;

/// <summary>
/// An energy-neutralizer hit. <paramref name="Outgoing"/> = you neuted a target; otherwise the
/// neut was applied to you. Direction comes from the gamelog line's lead colour, not the text (EVE writes no to/from
/// for energy warfare).
/// </summary>
public sealed record NeutEvent(
    DateTime Timestamp,
    bool Outgoing,
    int Amount) : GameLogEvent(Timestamp);
