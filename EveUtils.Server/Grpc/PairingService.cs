using EveUtils.Grpc;
using EveUtils.Server.Auth;
using EveUtils.Server.Transport;
using EveUtils.Shared.Modules.Esi;
using Grpc.Core;

namespace EveUtils.Server.Grpc;

/// <summary>
/// gRPC pairing endpoints. <see cref="Ping"/> proves the HTTP/2 + TLS transport stack. The
/// pairing methods live in the <c>PairingService.Pairing.cs</c> partial. The SSO code is
/// completed by <see cref="PairingCompleter"/> — either via the server's own SSO callback endpoint
/// (primary, when the server app has its own callback) or via the gRPC <c>RelayCode</c> loopback path.
/// </summary>
public sealed partial class PairingService(
    ServerCertificateInfo certificateInfo,
    ServerInfo serverInfo,
    EsiOptions esiOptions,
    PairingStateStore pairingStateStore,
    PairingCompleter pairingCompleter) : Pairing.PairingBase
{
    public override Task<PingReply> Ping(PingRequest request, ServerCallContext context)
    {
        return Task.FromResult(new PingReply
        {
            Message = $"pong: {request.Message}",
            ServerTime = DateTimeOffset.UtcNow.ToString("o"),
            CertFingerprint = certificateInfo.Fingerprint
        });
    }
}
