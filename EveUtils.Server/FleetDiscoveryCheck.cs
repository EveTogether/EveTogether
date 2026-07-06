using EveUtils.Grpc;
using EveUtils.Server.Grpc;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.Messaging;
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
/// Headless proof for fleet discovery, runnable via <c>--fleet-discovery-test</c>. Public fleets are
/// listable + directly joinable (idempotently); invite-only and archived fleets are not. Also asserts the
/// connected-characters source dedupes a character's multiple connections. Exit 0 = pass, 1 = fail.
/// </summary>
public static class FleetDiscoveryCheck
{
    private const int Creator = 1001;
    private const int Joiner = 2002;
    private const int Late = 3003;

    public static async Task<int> RunAsync(IServiceProvider services)
    {
        Console.WriteLine("== EVE-Utils discovery check (open fleets + join) ==");

        using var scope = services.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
        var repository = scope.ServiceProvider.GetRequiredService<IFleetRepository>();
        var ct = CancellationToken.None;
        var ok = true;

        var openFleet = await dispatcher.Send(new CreateFleetCommand(
            "Public Roam", null, FleetVisibility.Public, null, null, FleetOfflineBehavior.StayOffline, Creator), ct);
        var closedFleet = await dispatcher.Send(new CreateFleetCommand(
            "Closed Op", null, FleetVisibility.InviteOnly, null, null, FleetOfflineBehavior.StayOffline, Creator), ct);
        ok &= Check("create public + invite-only fleets", openFleet.IsSuccess && closedFleet.IsSuccess);
        var openId = openFleet.Value;
        var closedId = closedFleet.Value;

        // 1. Only the Public fleet is listed as open.
        var open = await dispatcher.Query(new ListOpenFleetsQuery(), ct);
        ok &= Check("open list contains the Public fleet", open.Any(f => f.Id == openId));
        ok &= Check("open list excludes the invite-only fleet", open.All(f => f.Id != closedId));

        // 2. Joining the Public fleet rosters the joiner; joining again is idempotent (no duplicate row).
        var join = await dispatcher.Send(new JoinFleetCommand(openId, Joiner), ct);
        ok &= Check("join public fleet", join.IsSuccess);
        var rejoin = await dispatcher.Send(new JoinFleetCommand(openId, Joiner), ct);
        ok &= Check("re-join is idempotent (success)", rejoin.IsSuccess);
        var members = await repository.ListMembersAsync(openId, ct);
        ok &= Check("joiner rostered exactly once", members.Count(m => m.CharacterId == Joiner) == 1);

        // 3. The invite-only fleet cannot be joined directly.
        var joinClosed = await dispatcher.Send(new JoinFleetCommand(closedId, Joiner), ct);
        ok &= Check("join invite-only rejected (PERMISSION_DENIED)",
            !joinClosed.IsSuccess && joinClosed.Messages.Any(m => m.Code == MessageCodes.PermissionDenied));

        // 4. Unknown fleet → NOT_FOUND.
        var joinGhost = await dispatcher.Send(new JoinFleetCommand(999_999_999, Joiner), ct);
        ok &= Check("join unknown fleet → NOT_FOUND",
            !joinGhost.IsSuccess && joinGhost.Messages.Any(m => m.Code == MessageCodes.NotFound));

        // 5. A disbanded fleet drops off the open list and refuses joins.
        var disband = await dispatcher.Send(new DisbandFleetCommand(openId, Creator), ct);
        ok &= Check("disband the public fleet", disband.IsSuccess);
        var openAfter = await dispatcher.Query(new ListOpenFleetsQuery(), ct);
        ok &= Check("disbanded fleet no longer open", openAfter.All(f => f.Id != openId));
        var joinArchived = await dispatcher.Send(new JoinFleetCommand(openId, Late), ct);
        ok &= Check("join archived fleet rejected (VALIDATION_FAILED)",
            !joinArchived.IsSuccess && joinArchived.Messages.Any(m => m.Code == MessageCodes.ValidationFailed));

        // 6. Connected-characters source dedupes a character's multiple connections.
        ok &= CheckConnectedCharacters();

        await CleanupAsync(scope.ServiceProvider, openId, ct);
        await CleanupAsync(scope.ServiceProvider, closedId, ct);

        Console.WriteLine(ok ? "RESULT: PASS ✓" : "RESULT: FAIL ✗");
        return ok ? 0 : 1;
    }

    private static bool CheckConnectedCharacters()
    {
        var clients = new ConnectedClients();
        clients.Add(new ConnectedClient("c1-a", 7001, "CharOne", new NullWriter()));
        clients.Add(new ConnectedClient("c1-b", 7001, "CharOne", new NullWriter())); // same char, 2nd connection
        clients.Add(new ConnectedClient("c2", 7002, "CharTwo", new NullWriter()));

        var connected = clients.ConnectedCharacters();
        return Check("connected characters dedupe multi-connection (2 distinct)",
            connected.Count == 2 && connected.Any(c => c.CharacterId == 7001) && connected.Any(c => c.CharacterId == 7002));
    }

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

    private sealed class NullWriter : IServerStreamWriter<ServerEnvelope>
    {
        public WriteOptions? WriteOptions { get; set; }
        public Task WriteAsync(ServerEnvelope message) => Task.CompletedTask;
        public Task WriteAsync(ServerEnvelope message, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
