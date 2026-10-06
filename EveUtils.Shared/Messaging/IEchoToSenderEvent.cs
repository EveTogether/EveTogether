namespace EveUtils.Shared.Messaging;

/// <summary>
/// Marker for a remote event whose server relay must include the connection that sent it. Existing events remain
/// sender-excluded unless they explicitly implement this contract.
/// </summary>
public interface IEchoToSenderEvent : IIntegrationEvent
{
}
