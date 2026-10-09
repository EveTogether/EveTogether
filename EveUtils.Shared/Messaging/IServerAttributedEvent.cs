namespace EveUtils.Shared.Messaging;

/// <summary>
/// Marker for a remote event whose sender the server fills in from the attached session, never from the client's
/// claim — the only sender a receiver may check against the fleet commander (ET-370, ET-494).
/// </summary>
public interface IServerAttributedEvent : IIntegrationEvent
{
}
