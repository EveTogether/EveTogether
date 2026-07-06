using EveUtils.Grpc;
using EveUtils.Server.Grpc;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Commands;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Queries;
using EveUtils.Shared.Modules.Fleet.Repositories;
using EveUtils.Shared.Modules.Messaging.Entities;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using FleetEntity = EveUtils.Shared.Modules.Fleet.Entities.Fleet;

namespace EveUtils.Server;

/// <summary>
/// Headless proof for the "one active fleet per character" rule + the Concluded lifecycle (2026-06-04), runnable
/// via <c>--fleet-active-guard-test</c>. Drives the real DI/dispatcher + the broadcast resolver to assert:
/// Conclude is creator-only and terminal (no re-start, no re-join) and frees its members from the active-lock;
/// the entry-guard blocks joining a *second* active fleet but still allows signing up to a Forming fleet; and
/// the broadcast tiebreak keeps a member who is started into a second active fleet coupled to the one they were
/// activated in first. Exit 0 = all passed.
/// </summary>
public static class FleetActiveGuardCheck
{
    private const int Owner1 = 7101;
    private const int Owner2 = 7202;
    private const int Pilot = 7303; // the character we move through the lifecycle + guard
    private const int Stranger = 7404;

    public static async Task<int> RunAsync(IServiceProvider services)
    {
        Console.WriteLine("== EVE-Utils one-active-fleet guard + Concluded lifecycle check ==");

        using var scope = services.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
        var repository = scope.ServiceProvider.GetRequiredService<IFleetRepository>();
        var ct = CancellationToken.None;
        var ok = true;
        var fleetIds = new List<long>();

        try
        {
            // ---- Part 1: Conclude lifecycle ----
            var f1 = (await dispatcher.Send(new CreateFleetCommand(
                "Op One", null, FleetVisibility.Public, null, null, FleetOfflineBehavior.StayOffline, Owner1), ct)).Value;
            fleetIds.Add(f1);
            ok &= Check("Pilot joins the first fleet", (await dispatcher.Send(new JoinFleetCommand(f1, Pilot), ct)).IsSuccess);

            var formingConclude = await dispatcher.Send(new ConcludeFleetCommand(f1, Owner1), ct);
            ok &= Check("Conclude on a Forming fleet rejected (ValidationFailed) — cancel via disband instead",
                !formingConclude.IsSuccess && formingConclude.Messages.Any(m => m.Code == MessageCodes.ValidationFailed));

            ok &= Check("creator starts it (Active)", (await dispatcher.Send(new StartFleetCommand(f1, Owner1), ct)).IsSuccess);

            var foreignConclude = await dispatcher.Send(new ConcludeFleetCommand(f1, Owner2), ct);
            ok &= Check("non-creator Conclude rejected (PERMISSION_DENIED)",
                !foreignConclude.IsSuccess && foreignConclude.Messages.Any(m => m.Code == MessageCodes.PermissionDenied));

            ok &= Check("creator can Conclude", (await dispatcher.Send(new ConcludeFleetCommand(f1, Owner1), ct)).IsSuccess);
            var concluded = await dispatcher.Query(new GetFleetQuery(f1), ct);
            ok &= Check("fleet is Concluded (and still not archived — kept for history)",
                concluded is { Activation: FleetActivation.Concluded, State: FleetState.Active });

            ok &= Check("Conclude is idempotent", (await dispatcher.Send(new ConcludeFleetCommand(f1, Owner1), ct)).IsSuccess);
            ok &= Check("a concluded fleet cannot be started again", !(await dispatcher.Send(new StartFleetCommand(f1, Owner1), ct)).IsSuccess);
            ok &= Check("a concluded fleet can no longer be joined", !(await dispatcher.Send(new JoinFleetCommand(f1, Stranger), ct)).IsSuccess);

            // ---- Part 2: entry-guard (one active fleet) — Pilot is free again now f1 is concluded ----
            var gActive = (await dispatcher.Send(new CreateFleetCommand(
                "Active Op G", null, FleetVisibility.Public, null, null, FleetOfflineBehavior.StayOffline, Owner2), ct)).Value;
            fleetIds.Add(gActive);
            ok &= Check("creator starts G (Active)", (await dispatcher.Send(new StartFleetCommand(gActive, Owner2), ct)).IsSuccess);
            ok &= Check("concluding f1 freed Pilot → he can join another active fleet G",
                (await dispatcher.Send(new JoinFleetCommand(gActive, Pilot), ct)).IsSuccess);

            var hActive = (await dispatcher.Send(new CreateFleetCommand(
                "Active Op H", null, FleetVisibility.Public, null, null, FleetOfflineBehavior.StayOffline, Owner1), ct)).Value;
            fleetIds.Add(hActive);
            ok &= Check("creator starts H (Active)", (await dispatcher.Send(new StartFleetCommand(hActive, Owner1), ct)).IsSuccess);
            var blocked = await dispatcher.Send(new JoinFleetCommand(hActive, Pilot), ct);
            ok &= Check("Pilot cannot join a SECOND active fleet (blocked, ValidationFailed)",
                !blocked.IsSuccess && blocked.Messages.Any(m => m.Code == MessageCodes.ValidationFailed));
            ok &= Check("Pilot is NOT a member of the second active fleet after the block",
                !await dispatcher.Query(new IsFleetMemberQuery(hActive, Pilot), ct));

            var iForming = (await dispatcher.Send(new CreateFleetCommand(
                "Planned Op I", null, FleetVisibility.Public, null, null, FleetOfflineBehavior.StayOffline, Owner1), ct)).Value;
            fleetIds.Add(iForming);
            ok &= Check("Pilot CAN sign up in advance to a Forming fleet while active elsewhere",
                (await dispatcher.Send(new JoinFleetCommand(iForming, Pilot), ct)).IsSuccess);

            // ---- Part 3: broadcast tiebreak (START-skip) ----
            // Pilot is now a member of active G (activated first) + Forming I. Start I → Pilot is in two active
            // fleets; he must stay coupled to G (activated earlier) and be skipped from the just-started I.
            ok &= await CheckTiebreakAsync(dispatcher, repository, gActive, iForming, ct);
        }
        finally
        {
            await CleanupAsync(scope.ServiceProvider, fleetIds, ct);
        }

        Console.WriteLine(ok ? "RESULT: PASS ✓" : "RESULT: FAIL ✗");
        return ok ? 0 : 1;
    }

    private static async Task<bool> CheckTiebreakAsync(
        IDispatcher dispatcher, IFleetRepository repository, long earlierActive, long laterForming, CancellationToken ct)
    {
        var clients = new ConnectedClients();
        clients.Add(new ConnectedClient("pilot-key", Pilot, "Pilot", new RecordingWriter()));
        var resolver = new FleetBroadcastResolver(repository, clients);

        var ok = Check("before its Start, the Forming fleet broadcasts nothing",
            (await resolver.ActiveBroadcastMembersAsync(laterForming, ct)).Count == 0);

        await dispatcher.Send(new StartFleetCommand(laterForming, Owner1), ct); // now both are Active

        ok &= Check("Pilot stays coupled to the fleet he was activated in first (broadcasts to G)",
            (await resolver.ActiveBroadcastMembersAsync(earlierActive, ct)).Contains(Pilot));
        ok &= Check("Pilot is NOT coupled to the newly-started fleet (skipped from I)",
            !(await resolver.ActiveBroadcastMembersAsync(laterForming, ct)).Contains(Pilot));
        return ok;
    }

    private static async Task CleanupAsync(IServiceProvider provider, IReadOnlyList<long> fleetIds, CancellationToken ct)
    {
        await using var db = await provider.GetRequiredService<IDbContextFactory<ServerDbContext>>().CreateDbContextAsync(ct);
        foreach (var id in fleetIds)
        {
            var fleet = await db.Set<FleetEntity>().FirstOrDefaultAsync(f => f.Id == id, ct);
            if (fleet is not null)
                db.Remove(fleet); // cascade removes members
        }
        await db.SaveChangesAsync(ct);

        int[] recipients = [Owner1, Owner2, Pilot, Stranger];
        await db.Set<QueuedMessage>().Where(m => recipients.Contains(m.RecipientCharacterId)).ExecuteDeleteAsync(ct);
    }

    private static bool Check(string label, bool pass)
    {
        Console.WriteLine($"  {(pass ? "✓" : "✗ FAIL")}  {label}");
        return pass;
    }

    private sealed class RecordingWriter : IServerStreamWriter<ServerEnvelope>
    {
        public WriteOptions? WriteOptions { get; set; }
        public Task WriteAsync(ServerEnvelope message) => Task.CompletedTask;
        public Task WriteAsync(ServerEnvelope message, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
