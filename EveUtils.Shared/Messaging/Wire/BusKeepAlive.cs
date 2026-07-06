using EveUtils.Grpc;

namespace EveUtils.Shared.Messaging.Wire;

/// <summary>
/// The reserved bus keepalive envelope. The server pushes one down every attached stream on a fixed
/// cadence so an idle client can still tell a live server from one that silently went away — a restart behind a
/// terminating proxy (cloudflared) keeps the HTTP/2 stream half-open, so transport keepalive is answered by the
/// tunnel edge and never reveals the dead origin. It carries no payload and is never turned into a domain event:
/// both ends recognise it by <see cref="EventType"/> and skip it after using its arrival as a liveness signal.
/// </summary>
public static class BusKeepAlive
{
    public const string EventType = "__keepalive";

    public static bool IsKeepAlive(EventEnvelope envelope) => envelope.EventType == EventType;
}
