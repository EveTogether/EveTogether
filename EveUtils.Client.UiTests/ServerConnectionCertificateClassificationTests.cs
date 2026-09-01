using System;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using EveUtils.Client.Messaging;
using Grpc.Core;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// The reconnect loop stops for a certificate the pin refuses and retries everything else (ET-95), so the line
/// between the two decides whether a link recovers on its own or waits for the user. It cannot be drawn on the gRPC
/// status code: a refused handshake arrives as <c>Internal</c>, which is also where everything gRPC cannot place ends
/// up. These pin the discriminant that does work — an <see cref="AuthenticationException"/> in the exception chain,
/// reached through <c>Status.DebugException</c> rather than <c>InnerException</c>.
/// </summary>
public class ServerConnectionCertificateClassificationTests
{
    private static RpcException Rpc(StatusCode code, Exception? debugException = null) =>
        new(new Status(code, "Error starting gRPC call.", debugException));

    [Fact]
    public void ARefusedCertificate_IsPermanent_EvenThoughItArrivesAsInternal()
    {
        // The chain measured against a real server presenting an unpinned certificate.
        var tls = new HttpRequestException(
            "The SSL connection could not be established, see inner exception.",
            new AuthenticationException(
                "The remote certificate was rejected by the provided RemoteCertificateValidationCallback."));

        Assert.True(ServerConnection.IsCertificateRejected(Rpc(StatusCode.Internal, tls)));
    }

    [Fact]
    public void ABareInternal_IsTransient()
    {
        // Internal is also gRPC's catch-all, so the status code on its own may never be read as permanent.
        Assert.False(ServerConnection.IsCertificateRejected(Rpc(StatusCode.Internal)));
    }

    [Fact]
    public void AServerThatIsSimplyDown_IsTransient()
    {
        var refused = new HttpRequestException("Connection refused", new SocketException(10061));

        Assert.False(ServerConnection.IsCertificateRejected(Rpc(StatusCode.Unavailable, refused)));
    }

    [Theory]
    [InlineData(StatusCode.PermissionDenied)]
    [InlineData(StatusCode.Unimplemented)]
    public void AStatusThatMerelySoundsPermanent_StaysTransient(StatusCode code)
    {
        // Both also come from a tunnel or proxy briefly serving something else, and calling them permanent would
        // leave a link that would have recovered on its own down until the user noticed (ET-95).
        Assert.False(ServerConnection.IsCertificateRejected(Rpc(code)));
    }

    [Fact]
    public void ATlsFailureOutsideAnRpcException_IsStillRecognised()
    {
        // ConnectAsync reports Ready before the handshake, but nothing guarantees the failure always surfaces
        // wrapped in an RpcException — the plain chain has to classify the same way.
        var direct = new HttpRequestException("The SSL connection could not be established.",
            new AuthenticationException("The remote certificate was rejected."));

        Assert.True(ServerConnection.IsCertificateRejected(direct));
    }

    [Fact]
    public void NoException_IsTransient()
    {
        Assert.False(ServerConnection.IsCertificateRejected(null));
    }
}
