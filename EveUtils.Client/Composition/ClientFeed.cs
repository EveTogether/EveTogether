using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Client.Gamelog;
using EveUtils.Client.Messaging;
using EveUtils.Client.Transport;
using EveUtils.Grpc;
using EveUtils.Shared.Transport;
using Microsoft.Extensions.DependencyInjection;
using GrpcPairing = EveUtils.Grpc.Pairing;

namespace EveUtils.Client.Composition;

/// <summary>
/// Headless full-chain demo (via <c>--feed [https://host:port] [seconds]</c>): pins the cert, uses
/// the dev session, attaches the remote bus and runs the synthetic feeder so a browser on
/// <c>/stream/dps</c> shows live DPS — the complete client → gRPC → server bus → SignalR chain.
/// </summary>
public static class ClientFeed
{
    public static async Task RunAsync(IServiceProvider services, string[] args)
    {
        var address = args.FirstOrDefault(a => a.StartsWith("https://", StringComparison.Ordinal)) ?? "https://localhost:7443";
        var seconds = args.Select(a => int.TryParse(a, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : 0)
            .FirstOrDefault(s => s > 0);
        if (seconds == 0)
            seconds = 20;

        var factory = services.GetRequiredService<GrpcChannelFactory>();
        var trustStore = services.GetRequiredService<IServerTrustStore>();
        var sessionStore = services.GetRequiredService<IClientSessionStore>();

        using (var pinning = factory.CreateForPairing(address))
        {
            var pairingClient = new GrpcPairing.PairingClient(pinning.Channel);
            await pairingClient.PingAsync(new PingRequest { Message = "pin" });
            var fingerprint = pinning.PresentedFingerprint();
            if (fingerprint is not null)
                trustStore.Pin(address, fingerprint);
        }

        await sessionStore.SaveAsync(address, new ClientSessionTokens("dev-test-token", "dev-test-token-refresh", "DevTester", 91000000));
        await services.GetRequiredService<IRemoteBusConnector>().AttachAsync(address);

        var gamelog = services.GetRequiredService<GamelogClientService>();
        gamelog.SetCharacter("DevTester");

        Console.WriteLine($"== feeding synthetic DPS to {address} for {seconds}s — open {address}/stream/dps to watch ==");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        try
        {
            await services.GetRequiredService<SyntheticDpsFeeder>().RunAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        Console.WriteLine("  feed done.");
    }
}
