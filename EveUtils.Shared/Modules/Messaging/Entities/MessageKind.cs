namespace EveUtils.Shared.Modules.Messaging.Entities;

/// <summary>
/// The kind of queued message. Open-for-extension: a new kind slots in without touching the
/// transport. <see cref="Mail"/> is a fire-and-forget notification; <see cref="FleetInvite"/> wraps a durable
/// fleet invite and carries an Accept/Decline response; <see cref="FleetJoinRequest"/> wraps a durable
/// request-to-join an invite-only fleet, answered by the fleet owner; <see cref="FleetStarted"/> announces that a
/// fleet you are in went active and carries the fleet id (RefId) so the client can offer "open metrics".
/// </summary>
public enum MessageKind
{
    Mail = 0,
    FleetInvite = 1,
    FleetJoinRequest = 2,
    FleetStarted = 3
}
