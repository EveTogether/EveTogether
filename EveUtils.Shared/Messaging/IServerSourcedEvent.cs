namespace EveUtils.Shared.Messaging;

/// <summary>
/// An integration event whose source server is known only at the client transport boundary: the event
/// payload is server-serialized and a self-hosted server doesn't know the address the client reached it on, so the
/// receiving connection stamps it after deserialization. The inbox uses it to answer a delivered message on the
/// server it actually came from rather than the first coupled server.
/// </summary>
public interface IServerSourcedEvent
{
    /// <summary>The address of the server this event was received from — set by the client's receive loop, null
    /// for events not sourced from a server connection.</summary>
    string? SourceServerAddress { get; set; }
}
