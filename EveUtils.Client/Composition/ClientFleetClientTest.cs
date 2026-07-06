using System;
using System.Linq;
using System.Threading.Tasks;
using EveUtils.Client.Transport;
using EveUtils.Grpc;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Transport;
using Microsoft.Extensions.DependencyInjection;
using GrpcPairing = EveUtils.Grpc.Pairing;

namespace EveUtils.Client.Composition;

/// <summary>
/// Headless end-to-end check of the client <see cref="FleetClient"/> gRPC wrapper against a running server,
/// via <c>--fleet-client-test</c>. Pins the cert + uses the seeded DevTester session, then drives the full
/// surface — create, list-mine, discover, join, enter, leave, invite, edit, disband — asserting the server's real
/// accept/deny result each step. Proves increment 1's wrapper for real (not just compile). Exit 0 = pass, 1 = fail.
/// </summary>
public static class ClientFleetClientTest
{
    private const int DevTester2 = 91000001;

    public static async Task<int> RunAsync(IServiceProvider services, string[] args)
    {
        Console.WriteLine("== EVE-Utils FleetClient end-to-end check ==");
        var address = args.FirstOrDefault(a => a.StartsWith("https://", StringComparison.Ordinal)) ?? "https://localhost:7443";

        var factory = services.GetRequiredService<GrpcChannelFactory>();
        var trustStore = services.GetRequiredService<IServerTrustStore>();
        var sessionStore = services.GetRequiredService<IClientSessionStore>();
        var fleets = services.GetRequiredService<FleetClient>();

        using (var pinning = factory.CreateForPairing(address))
        {
            var pairingClient = new GrpcPairing.PairingClient(pinning.Channel);
            await pairingClient.PingAsync(new PingRequest { Message = "pin" });
            var fingerprint = pinning.PresentedFingerprint();
            if (fingerprint is not null)
                trustStore.Pin(address, fingerprint);
        }
        await sessionStore.SaveAsync(address, new ClientSessionTokens("dev-test-token", "dev-test-token-refresh", "DevTester", 91000000));

        var ok = true;

        var created = await fleets.CreateFleetAsync(
            address, "ClientTest", "wrapper e2e", FleetVisibility.Public, FleetOfflineBehavior.StayOffline, null, null);
        ok &= Check("create fleet", created.Ok && created.FleetId != 0);
        var fleetId = created.FleetId;

        ok &= Check("fleet appears in my fleets", (await fleets.ListMyFleetsAsync(address)).Any(f => f.Id == fleetId));
        ok &= Check("public fleet listed in discovery", (await fleets.ListOpenFleetsAsync(address)).Any(f => f.Id == fleetId));

        ok &= Check("join own public fleet (idempotent)", (await fleets.JoinFleetAsync(address, fleetId)).Ok);
        ok &= Check("enter fleet (activity stamp)", (await fleets.EnterFleetAsync(address, fleetId)).Ok);
        // Leave is now a real roster-leave; the creator can't leave their own fleet until ownership is transferred.
        ok &= Check("creator cannot leave their own fleet (transfer required)", !(await fleets.LeaveFleetAsync(address, fleetId)).Ok);

        var invited = await fleets.CreateInviteAsync(address, fleetId, DevTester2, FleetRole.SquadMember);
        ok &= Check("create invite for the other dev character", invited.Ok && invited.InviteId != 0);

        ok &= Check("edit fleet", (await fleets.EditFleetAsync(
            address, fleetId, "ClientTest2", "edited", FleetVisibility.InviteOnly, FleetOfflineBehavior.StayOffline, null, null)).Ok);

        ok &= Check("creator can still enter their now invite-only fleet", (await fleets.EnterFleetAsync(address, fleetId)).Ok);

        ok &= Check("disband fleet", (await fleets.DisbandFleetAsync(address, fleetId)).Ok);
        ok &= Check("disbanded fleet no longer in my active fleets",
            (await fleets.ListMyFleetsAsync(address)).All(f => f.Id != fleetId || f.State != FleetState.Active));

        Console.WriteLine(ok ? "RESULT: PASS ✓" : "RESULT: FAIL ✗");
        return ok ? 0 : 1;
    }

    private static bool Check(string label, bool pass)
    {
        Console.WriteLine($"  {(pass ? "✓" : "✗ FAIL")}  {label}");
        return pass;
    }
}
