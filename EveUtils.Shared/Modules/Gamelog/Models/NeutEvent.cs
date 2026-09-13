namespace EveUtils.Shared.Modules.Gamelog.Models;

/// <summary>
/// An energy-neutralizer hit. <paramref name="Outgoing"/> = you neuted a target; otherwise the
/// neut was applied to you. Direction comes from the gamelog line's lead colour, not the text (EVE writes no to/from
/// for energy warfare). <paramref name="Source"/> is the rest of the line as written — the other ship and the module —
/// which tells one neutralizer's cycle from another's (ET-277).
/// </summary>
public sealed record NeutEvent(
    DateTime Timestamp,
    bool Outgoing,
    int Amount,
    string? Source = null) : GameLogEvent(Timestamp);
