namespace EveUtils.Shared.Messaging;

/// <summary>
/// Optional marker for an event aimed at a single character rather than broadcast to all connected
/// clients. The transport copies <see cref="TargetCharacterId"/> onto the wire envelope's
/// <c>target_character_id</c>; the server then reroutes the event only to that character's connections.
/// Events that do not implement this interface keep the existing broadcast behavior.
/// </summary>
public interface ITargetedEvent : IIntegrationEvent
{
    /// <summary>ESI id of the recipient character. Must be non-zero for targeted delivery.</summary>
    int TargetCharacterId { get; }
}
