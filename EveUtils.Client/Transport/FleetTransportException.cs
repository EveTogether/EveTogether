using System;

namespace EveUtils.Client.Transport;

/// <summary>
/// A fleet query could not reach the server (a transport-level failure, e.g. the server is briefly unreachable).
/// Surfaced by the list endpoints that back the Fleets window so a transient error is distinguishable from a
/// genuinely empty result — the UI then keeps the previous list instead of silently blanking it. Transport-agnostic
/// on purpose (no gRPC type leaks past the <see cref="IFleetTransportClient"/> seam).
/// </summary>
public sealed class FleetTransportException(string message) : Exception(message);
