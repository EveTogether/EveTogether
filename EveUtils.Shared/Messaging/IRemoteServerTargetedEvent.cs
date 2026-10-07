namespace EveUtils.Shared.Messaging;

/// <summary>
/// Client-originated remote traffic that belongs to one coupled server. The address is transport routing metadata and
/// is not part of the wire payload received by that server.
/// </summary>
public interface IRemoteServerTargetedEvent
{
    string ServerAddress { get; }
}
