using EveUtils.Grpc;
using EveUtils.Server.Grpc;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.Modules.Fleet.Commands;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Queries;
using EveUtils.Shared.Modules.Fleet.Repositories;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using FleetEntity = EveUtils.Shared.Modules.Fleet.Entities.Fleet;

namespace EveUtils.Server;

/// <summary>
/// Headless proof for the fleet-scoped routing seam, now server-authoritative, runnable via
/// <c>--fleet-participation-test</c>. Asserts: the membership gate, that the live broadcast set is exactly the
/// fleet's roster members who are connected (membership ∩ presence via <see cref="FleetBroadcastResolver"/>),
/// that a fleet-scoped event reaches only that set (sender excluded), and that a disconnect drops a member from it.
/// Exit 0 = pass, 1 = fail.
/// </summary>
public static class FleetParticipationCheck
{
    private const int Creator = 1001;
    private const int A = 2002;
    private const int B = 3003;
    private const int E = 5005;

    public static async Task<int> RunAsync(IServiceProvider services)
    {
        Console.WriteLine("== EVE-Utils fleet-scoped routing check (server-authoritative) ==");

        using var scope = services.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
        var repository = scope.ServiceProvider.GetRequiredService<IFleetRepository>();
        var ct = CancellationToken.None;
        var ok = true;
        long fleetId = 0;

        try
        {
            // --- Membership gate (what participation requires). ---
            var fleet = await dispatcher.Send(new CreateFleetCommand(
                "Op Fleet", null, FleetVisibility.Public, null, null, FleetOfflineBehavior.StayOffline, Creator), ct);
            fleetId = fleet.Value;
            ok &= Check("create fleet", fleet.IsSuccess);

            ok &= Check("join A + B", (await dispatcher.Send(new JoinFleetCommand(fleetId, A), ct)).IsSuccess
                                      && (await dispatcher.Send(new JoinFleetCommand(fleetId, B), ct)).IsSuccess);
            ok &= Check("member is a member (IsFleetMember true)", await dispatcher.Query(new IsFleetMemberQuery(fleetId, A), ct));
            ok &= Check("non-member is not a member (IsFleetMember false)", !await dispatcher.Query(new IsFleetMemberQuery(fleetId, E), ct));

            // --- Broadcast set = roster members ∩ connected of an ACTIVE fleet + fleet-scoped routing. ---
            ok &= await CheckBroadcastAsync(dispatcher, repository, fleetId, ct);

            // --- Disband → archived. ---
            await dispatcher.Send(new DisbandFleetCommand(fleetId, Creator), ct);
            var archived = await dispatcher.Query(new GetFleetQuery(fleetId), ct);
            ok &= Check("disbanded fleet is no longer Active", archived is { State: FleetState.Archived });
        }
        finally
        {
            if (fleetId != 0)
                await CleanupAsync(scope.ServiceProvider, fleetId, ct);
        }

        Console.WriteLine(ok ? "RESULT: PASS ✓" : "RESULT: FAIL ✗");
        return ok ? 0 : 1;
    }

    private static async Task<bool> CheckBroadcastAsync(IDispatcher dispatcher, IFleetRepository repository, long fleetId, CancellationToken ct)
    {
        var clients = new ConnectedClients();
        var aWriter = new RecordingWriter();
        var bWriter = new RecordingWriter();
        var eWriter = new RecordingWriter();
        clients.Add(new ConnectedClient("a-key", A, "A", aWriter));
        clients.Add(new ConnectedClient("b-key", B, "B", bWriter));
        clients.Add(new ConnectedClient("e-key", E, "E", eWriter)); // connected but NOT a roster member

        var resolver = new FleetBroadcastResolver(repository, clients);

        // A Forming fleet (members signed up in advance, not yet started) broadcasts nothing.
        var ok = Check("Forming fleet → empty broadcast set (no live sharing before Start)",
            (await resolver.ActiveBroadcastMembersAsync(fleetId, ct)).Count == 0);

        // Start the fleet → it becomes Active and its connected members form the broadcast set.
        await dispatcher.Send(new StartFleetCommand(fleetId, Creator), ct);

        var participants = await resolver.ActiveBroadcastMembersAsync(fleetId, ct);
        var set = Set(participants);

        ok &= Check("Active fleet broadcast set = connected members (A + B present)", set.IsSupersetOf([A, B]));
        ok &= Check("broadcast set excludes a connected non-member (E)", !set.Contains(E));

        // Fleet-scoped routing over the resolved set, sender excluded (no echo).
        var env = new EventEnvelope { EventType = "fleet.metric", EventId = "m1", PayloadJson = "{}", FleetId = fleetId };
        await clients.SendToCharactersAsync(participants, env, ct, exceptKey: "a-key");
        ok &= Check("fleet-scoped → member B received", bWriter.Count == 1);
        ok &= Check("fleet-scoped → sender A excluded (no echo)", aWriter.Count == 0);
        ok &= Check("fleet-scoped → non-member E NOT received", eWriter.Count == 0);

        // Disconnect drops a member from the broadcast set (leave-by-any-means).
        clients.Remove("b-key");
        ok &= Check("disconnected member drops from the broadcast set",
            !Set(await resolver.ActiveBroadcastMembersAsync(fleetId, ct)).Contains(B));
        return ok;
    }

    private static HashSet<int> Set(IReadOnlyList<int> items) => [.. items];

    private static async Task CleanupAsync(IServiceProvider provider, long fleetId, CancellationToken ct)
    {
        await using var db = await provider.GetRequiredService<IDbContextFactory<ServerDbContext>>().CreateDbContextAsync(ct);
        var fleet = await db.Set<FleetEntity>().FirstOrDefaultAsync(f => f.Id == fleetId, ct);
        if (fleet is not null)
        {
            db.Remove(fleet);
            await db.SaveChangesAsync(ct);
        }
    }

    private static bool Check(string label, bool pass)
    {
        Console.WriteLine($"  {(pass ? "✓" : "✗ FAIL")}  {label}");
        return pass;
    }

    private sealed class RecordingWriter : IServerStreamWriter<ServerEnvelope>
    {
        private int _count;
        public int Count => _count;
        public WriteOptions? WriteOptions { get; set; }
        public Task WriteAsync(ServerEnvelope message) { _count++; return Task.CompletedTask; }
        public Task WriteAsync(ServerEnvelope message, CancellationToken cancellationToken) { _count++; return Task.CompletedTask; }
    }
}
