using System;
using System.Linq;
using System.Threading.Tasks;
using EveUtils.Client.Messaging;
using EveUtils.Client.Transport;
using EveUtils.Grpc;
using EveUtils.Shared.Transport;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Gamelog.Dtos;
using EveUtils.Shared.Modules.Gamelog.Events;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using GrpcEventBus = EveUtils.Grpc.EventBusStream;
using GrpcPairing = EveUtils.Grpc.Pairing;

namespace EveUtils.Client.Composition;

/// <summary>
/// Headless end-to-end check of the auth-gated remote event bus, via
/// <c>--remote-test [https://host:port] [sessionToken]</c>. Pins the cert, stores a (dev) session,
/// attaches the real <see cref="RemoteBusConnectionManager"/>, publishes a few DPS samples
/// (EventTarget.Both → over the wire) and verifies a bogus token is rejected (Unauthenticated).
/// </summary>
public static class ClientRemoteTest
{
    public static async Task RunAsync(IServiceProvider services, string[] args)
    {
        var address = args.FirstOrDefault(a => a.StartsWith("https://", StringComparison.Ordinal)) ?? "https://localhost:7443";
        var token = args.SkipWhile(a => a != "--remote-test").Skip(1)
            .FirstOrDefault(a => !a.StartsWith("https://", StringComparison.Ordinal)) ?? "dev-test-token";

        var factory = services.GetRequiredService<GrpcChannelFactory>();
        var trustStore = services.GetRequiredService<IServerTrustStore>();
        var sessionStore = services.GetRequiredService<IClientSessionStore>();
        var connector = services.GetRequiredService<IRemoteBusConnector>();
        var bus = services.GetRequiredService<IEventBus>();

        Console.WriteLine($"== EVE-Utils remote-bus test → {address} ==");

        // 1. Pin the cert (TOFU).
        using (var pinning = factory.CreateForPairing(address))
        {
            var pairingClient = new GrpcPairing.PairingClient(pinning.Channel);
            await pairingClient.PingAsync(new PingRequest { Message = "pin" });
            var fingerprint = pinning.PresentedFingerprint();
            if (fingerprint is not null)
                trustStore.Pin(address, fingerprint);
        }
        Console.WriteLine("  cert pinned ✓");

        // 2. Negative: a bogus session token must be rejected by the auth gate.
        var pinnedChannel = factory.CreatePinned(address);
        var rejected = false;
        try
        {
            var ebClient = new GrpcEventBus.EventBusStreamClient(pinnedChannel);
            using var bad = ebClient.Attach(new Metadata { { "authorization", "Bearer bogus-token" } });
            await bad.RequestStream.WriteAsync(new ClientEnvelope { Event = new EventEnvelope { EventType = "gamelog.combat", PayloadJson = "{}" } });
            await bad.ResponseStream.MoveNext();
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unauthenticated)
        {
            rejected = true;
        }
        Console.WriteLine(rejected ? "  auth gate ✓ (bogus token rejected)" : "  AUTH GATE FAIL: bogus token accepted!");

        // 3. Happy path: store the (dev) session, attach the real transport, publish over the wire.
        await sessionStore.SaveAsync(address, new ClientSessionTokens(token, token + "-refresh", "DevTester", 91000000));
        await connector.AttachAsync(address);
        Console.WriteLine("  attached with session token ✓; publishing 3 DPS samples (EventTarget.Both)…");

        for (var i = 1; i <= 3; i++)
        {
            var dto = new DpsSampleDto(91000000, "DevTester", 100 * i, 20 * i, DateTimeOffset.UtcNow);
            await bus.PublishAsync(new CombatLoggedEvent(dto, 91000000), EventTarget.Both);
            await Task.Delay(300);
        }

        await Task.Delay(500);
        Console.WriteLine("  done — check the server log for 'Remote DPS from DevTester'.");
    }
}
