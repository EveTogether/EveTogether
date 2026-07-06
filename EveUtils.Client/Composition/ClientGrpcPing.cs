using System;
using System.Linq;
using System.Threading.Tasks;
using EveUtils.Client.Transport;
using EveUtils.Grpc;
using EveUtils.Shared.Transport;
using Microsoft.Extensions.DependencyInjection;
using GrpcPairing = EveUtils.Grpc.Pairing;

namespace EveUtils.Client.Composition;

/// <summary>
/// Headless verification of the gRPC transport stack (via <c>--grpc-ping [https://host:port]</c>):
/// first contact captures + pins the server's self-signed cert fingerprint (TOFU), then a pinned
/// reconnect proves HTTP/2 gRPC works over the self-signed TLS endpoint.
/// </summary>
public static class ClientGrpcPing
{
    public static async Task RunAsync(IServiceProvider services, string[] args)
    {
        var address = args.FirstOrDefault(a => a.StartsWith("https://", StringComparison.Ordinal))
                      ?? "https://localhost:7443";

        var factory = services.GetRequiredService<GrpcChannelFactory>();
        var trustStore = services.GetRequiredService<IServerTrustStore>();

        Console.WriteLine($"== EVE-Utils gRPC ping (TOFU) → {address} ==");

        using var pairing = factory.CreateForPairing(address);
        var client = new GrpcPairing.PairingClient(pairing.Channel);
        var reply = await client.PingAsync(new PingRequest { Message = "hello" });
        var presented = pairing.PresentedFingerprint();

        Console.WriteLine($"  reply: {reply.Message} @ {reply.ServerTime}");
        Console.WriteLine($"  server-reported cert fp: {reply.CertFingerprint}");
        Console.WriteLine($"  TLS-presented   cert fp: {presented}");

        if (presented is null)
        {
            Console.WriteLine("  FAIL: no presented fingerprint captured.");
            return;
        }

        trustStore.Pin(address, presented);
        Console.WriteLine("  pinned ✓");

        var pinnedChannel = factory.CreatePinned(address); // factory-owned (cached + disposed by the factory)
        var pinnedClient = new GrpcPairing.PairingClient(pinnedChannel);
        var pinnedReply = await pinnedClient.PingAsync(new PingRequest { Message = "pinned" });
        Console.WriteLine($"  pinned ping: {pinnedReply.Message} ✓ (HTTP/2 gRPC over self-signed TLS)");
    }
}
