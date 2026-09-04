namespace EveUtils.Shared.Modules.Fleet.Enums;

/// <summary>
/// What stopped a fleet (ET-167). A stop is always the same transition — <c>Active</c> → <c>Forming</c>, roster
/// intact — but a pilot reads "the FC stopped the op" very differently from "the op stopped because nobody was left",
/// so the reason travels with the stop instead of being reconstructed from a timestamp afterwards.
///
/// <see cref="Manual"/> is 0 deliberately: this rides the wire on <c>FleetChangePayload</c> and on
/// <see cref="Commands.StopFleetCommand"/> between a client and a server that update separately, so a payload written
/// before this existed decodes as the manual stop it was.
/// </summary>
public enum FleetStopTrigger
{
    /// <summary>The owner pressed STOP (ET-166) — the only kind that existed before the automatic stop.</summary>
    Manual = 0,

    /// <summary>Every member had left the roster. Fires plainly, downtime included: whoever left stayed gone.</summary>
    RosterEmpty = 1,

    /// <summary>Every member's client had gone silent past <c>FleetMemberPresence.SilentAfter</c>. The one trigger
    /// that reads false-positive around a restart, and so the only one held back by
    /// <see cref="Cleanup.FleetAutoStopBrake"/>.</summary>
    AllMembersOffline = 2,
}
