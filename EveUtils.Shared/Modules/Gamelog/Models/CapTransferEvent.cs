namespace EveUtils.Shared.Modules.Gamelog.Models;

/// <summary>
/// A remote-capacitor-transfer hit. <paramref name="Outgoing"/> = you transmitted cap to a
/// fleetmate; otherwise cap was transmitted to you. Direction comes from the "to"/"by" keyword in the gamelog line.
/// </summary>
public sealed record CapTransferEvent(
    DateTime Timestamp,
    bool Outgoing,
    int Amount) : GameLogEvent(Timestamp);
