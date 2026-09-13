namespace EveUtils.Shared.Modules.Gamelog.Models;

/// <summary>
/// A remote-capacitor-transfer hit. <paramref name="Outgoing"/> = you transmitted cap to a
/// fleetmate; otherwise cap was transmitted to you. Direction comes from the "to"/"by" keyword in the gamelog line.
/// <paramref name="Source"/> is the rest of the line as written — the other pilot and the module — which tells one
/// transmitter's cycle from another's (ET-277).
/// </summary>
public sealed record CapTransferEvent(
    DateTime Timestamp,
    bool Outgoing,
    int Amount,
    string? Source = null) : GameLogEvent(Timestamp);
