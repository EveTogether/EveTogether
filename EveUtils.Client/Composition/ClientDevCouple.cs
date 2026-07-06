using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using EveUtils.Client.Transport;
using EveUtils.Grpc;
using EveUtils.Shared.Transport;
using Microsoft.Extensions.DependencyInjection;
using GrpcPairing = EveUtils.Grpc.Pairing;

namespace EveUtils.Client.Composition;

/// <summary>
/// Development-only shortcut (via <c>--dev-couple &lt;token&gt; &lt;charName&gt; &lt;charId&gt; [https://host]</c>): pins the
/// server cert (TOFU) and stores a client session for one of the server's seeded dev characters, then exits — no
/// EVE SSO. Run once per instance (<c>EVEUTILS_INSTANCE=A/B</c>) so the GUI restores the connection on next start.
/// Lets two client instances connect at once for the fleet two-client scenarios.
/// </summary>
public static class ClientDevCouple
{
    public static async Task RunAsync(IServiceProvider services, string[] args)
    {
        var positional = args.SkipWhile(a => !string.Equals(a, "--dev-couple", StringComparison.Ordinal)).Skip(1)
            .Where(a => !a.StartsWith("https://", StringComparison.Ordinal)).ToArray();
        var token = positional.ElementAtOrDefault(0) ?? "dev-test-token";
        var name = positional.ElementAtOrDefault(1) ?? "DevTester";
        var id = int.TryParse(positional.ElementAtOrDefault(2), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 91000000;
        var address = args.FirstOrDefault(a => a.StartsWith("https://", StringComparison.Ordinal)) ?? "https://localhost:7443";

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

        await sessionStore.SaveAsync(address, new ClientSessionTokens(token, token + "-refresh", name, id));
        Console.WriteLine($"Dev-coupled {name} ({id}) to {address}. Start the GUI to connect.");
    }
}
