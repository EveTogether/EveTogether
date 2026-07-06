using System;
using Grpc.Net.Client;

namespace EveUtils.Client.Transport;

/// <summary>
/// A first-contact gRPC channel that accepts any server cert but records the presented fingerprint,
/// so the caller can pin it (TOFU) after a successful pairing.
/// </summary>
public sealed record PinningChannel(GrpcChannel Channel, Func<string?> PresentedFingerprint) : IDisposable
{
    public void Dispose() => Channel.Dispose();
}
