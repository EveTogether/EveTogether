using EveUtils.Grpc;
using EveUtils.Server.Grpc;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Modules.Fleet.Cleanup;
using EveUtils.Shared.Modules.Fleet.Commands;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Queries;
using EveUtils.Shared.Modules.Fleet.Repositories;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Server;

/// <summary>
/// Headless proof for the persistence + cleanup rule, runnable via <c>--fleet-cleanup-test</c>. Two
/// layers: the pure <see cref="FleetCleanupPolicy"/> decision table (active-participant protection, inactivity
/// grace, end-time acceleration, archive→hard-delete window) and an integration sweep through the real
/// <see cref="FleetCleanupRunner"/> + DB + <c>ActiveFleetRegistry</c> that archives an inactive fleet and
/// hard-deletes a long-archived one. Exit 0 = pass, 1 = fail.
/// </summary>
public static class FleetCleanupCheck
{
    private const int Creator = 7001;
    private const int Member = 8002;

    public static async Task<int> RunAsync(IServiceProvider services)
    {
        Console.WriteLine("== EVE-Utils fleet persistence + cleanup check ==");
        var ok = EvaluatePolicy();
        ok &= await EvaluateSweepAsync(services);
        Console.WriteLine(ok ? "RESULT: PASS ✓" : "RESULT: FAIL ✗");
        return ok ? 0 : 1;
    }

    private static bool EvaluatePolicy()
    {
        var opts = FleetCleanupOptions.Default;
        var now = DateTimeOffset.UnixEpoch.AddYears(56); // fixed clock, no wall-time dependency
        var recent = now - TimeSpan.FromMinutes(5);
        var stale = now - TimeSpan.FromHours(1);
        var longAgo = now - TimeSpan.FromHours(25);

        // A non-concluded fleet is never auto-archived, no matter how stale or how long past its end-time:
        // a Forming fleet planned days ahead must survive for members to sign up and fit.
        var ok = Check("forming + no participant + stale activity → kept (never auto-archived)",
            FleetCleanupPolicy.Evaluate(FleetState.Active, FleetActivation.Forming, null, stale, false, now, opts) == FleetCleanupAction.None);
        ok &= Check("forming + no participant + end-time passed → still kept",
            FleetCleanupPolicy.Evaluate(FleetState.Active, FleetActivation.Forming, now - TimeSpan.FromMinutes(1), stale, false, now, opts) == FleetCleanupAction.None);
        ok &= Check("in-game-active + no participant + stale activity → kept (never auto-archived)",
            FleetCleanupPolicy.Evaluate(FleetState.Active, FleetActivation.Active, null, stale, false, now, opts) == FleetCleanupAction.None);

        // Only a concluded op is swept on the inactivity/end-time rule.
        ok &= Check("concluded + an active participant → kept (timestamps irrelevant)",
            FleetCleanupPolicy.Evaluate(FleetState.Active, FleetActivation.Concluded, null, longAgo, hasActiveParticipants: true, now, opts) == FleetCleanupAction.None);
        ok &= Check("concluded + no participant + recent activity → kept",
            FleetCleanupPolicy.Evaluate(FleetState.Active, FleetActivation.Concluded, null, recent, false, now, opts) == FleetCleanupAction.None);
        ok &= Check("concluded + no participant + stale activity → archived",
            FleetCleanupPolicy.Evaluate(FleetState.Active, FleetActivation.Concluded, null, stale, false, now, opts) == FleetCleanupAction.Archive);
        ok &= Check("concluded + no participant + end-time passed → archived even when recent (acceleration)",
            FleetCleanupPolicy.Evaluate(FleetState.Active, FleetActivation.Concluded, now - TimeSpan.FromMinutes(1), recent, false, now, opts) == FleetCleanupAction.Archive);
        ok &= Check("concluded + no participant + end-time still in future → kept",
            FleetCleanupPolicy.Evaluate(FleetState.Active, FleetActivation.Concluded, now + TimeSpan.FromHours(1), recent, false, now, opts) == FleetCleanupAction.None);
        ok &= Check("archived + within keep-window → kept",
            FleetCleanupPolicy.Evaluate(FleetState.Archived, FleetActivation.Concluded, null, stale, false, now, opts) == FleetCleanupAction.None);
        ok &= Check("archived + past keep-window → hard-deleted",
            FleetCleanupPolicy.Evaluate(FleetState.Archived, FleetActivation.Concluded, null, longAgo, false, now, opts) == FleetCleanupAction.Delete);
        return ok;
    }

    private static async Task<bool> EvaluateSweepAsync(IServiceProvider services)
    {
        var opts = FleetCleanupOptions.Default;
        using var scope = services.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
        var repo = scope.ServiceProvider.GetRequiredService<IFleetRepository>();
        var runner = scope.ServiceProvider.GetRequiredService<FleetCleanupRunner>();
        var connected = services.GetRequiredService<ConnectedClients>();
        var ct = CancellationToken.None;
        var ok = true;
        var created = new List<long>();

        try
        {
            // A freshly created fleet (LastActivityAt = now) survives a sweep — it isn't stale yet.
            var fresh = (await dispatcher.Send(new CreateFleetCommand(
                "Fresh", null, FleetVisibility.Public, null, null, FleetOfflineBehavior.StayOffline, Creator), ct)).Value;
            created.Add(fresh);
            var now = DateTimeOffset.UtcNow;
            await runner.SweepAsync(now, opts, ct);
            ok &= Check("fresh fleet survives the sweep", (await repo.GetAsync(fresh, ct))?.State == FleetState.Active);

            // rev: a Forming fleet (e.g. one planned days ahead) is NEVER auto-archived, even when fully stale —
            // members must be able to sign up and fit in the meantime. Only an explicit Conclude (or disband) removes it.
            await repo.TouchActivityAsync(fresh, now - TimeSpan.FromHours(2), ct);
            var formingSweep = await runner.SweepAsync(now, opts, ct);
            ok &= Check("stale forming fleet survives the sweep (never auto-archived)", (await repo.GetAsync(fresh, ct))?.State == FleetState.Active);
            ok &= Check("sweep archives nothing while only forming fleets are stale", formingSweep.Archived == 0);

            // Once concluded, the same stale fleet IS archived (soft-delete) on the next sweep.
            await ConcludeAsync(repo, fresh, ct);
            await repo.TouchActivityAsync(fresh, now - TimeSpan.FromHours(2), ct);
            var swept = await runner.SweepAsync(now, opts, ct);
            ok &= Check("stale concluded fleet is archived by the sweep", (await repo.GetAsync(fresh, ct))?.State == FleetState.Archived);
            ok &= Check("sweep reports the archive", swept.Archived >= 1);

            // An active participant protects a stale concluded fleet from archiving.
            var guarded = (await dispatcher.Send(new CreateFleetCommand(
                "Guarded", null, FleetVisibility.Public, null, null, FleetOfflineBehavior.StayOffline, Creator), ct)).Value;
            created.Add(guarded);
            await dispatcher.Send(new JoinFleetCommand(guarded, Member), ct);
            await ConcludeAsync(repo, guarded, ct);
            await repo.TouchActivityAsync(guarded, now - TimeSpan.FromHours(2), ct);
            connected.Add(new ConnectedClient("cleanup-member", Member, "Member", new NoopWriter())); // a connected roster member
            await runner.SweepAsync(now, opts, ct);
            ok &= Check("connected member protects a stale concluded fleet", (await repo.GetAsync(guarded, ct))?.State == FleetState.Active);

            // Once the member disconnects (leaves the broadcast set), the next sweep archives it.
            connected.Remove("cleanup-member");
            await repo.TouchActivityAsync(guarded, now - TimeSpan.FromHours(2), ct);
            await runner.SweepAsync(now, opts, ct);
            ok &= Check("after the participant leaves, the stale concluded fleet is archived", (await repo.GetAsync(guarded, ct))?.State == FleetState.Archived);

            // FleetActivityTracker: live fleet traffic refreshes the activity clock, so an actively-played
            // fleet survives the sweep even with NO live connection — combat never goes through a roster command, which
            // is what used to let a fleet be archived during a brief client restart mid-fight. (Concluded so the
            // activity-clock refresh, not the never-archive-while-forming rule, is what keeps it alive.)
            var tracker = services.GetRequiredService<FleetActivityTracker>();
            var active = (await dispatcher.Send(new CreateFleetCommand(
                "Active", null, FleetVisibility.Public, null, null, FleetOfflineBehavior.StayOffline, Creator), ct)).Value;
            created.Add(active);
            await ConcludeAsync(repo, active, ct);
            await repo.TouchActivityAsync(active, now - TimeSpan.FromHours(2), ct); // stale: no roster event for 2h
            await tracker.NoteAsync(active, now, ct);                                // a live metric sample arrives
            ok &= Check("live fleet traffic refreshes the activity clock",
                (await repo.GetAsync(active, ct))?.LastActivityAt >= now - TimeSpan.FromSeconds(1));
            await runner.SweepAsync(now, opts, ct);
            ok &= Check("an actively-measured fleet survives the sweep with no live connection",
                (await repo.GetAsync(active, ct))?.State == FleetState.Active);

            // Throttle: a second note within the window does not move the clock again (caps DB writes ~1/min/fleet).
            await tracker.NoteAsync(active, now + TimeSpan.FromSeconds(5), ct);
            ok &= Check("activity notes are throttled (~1/min)",
                (await repo.GetAsync(active, ct))?.LastActivityAt < now + TimeSpan.FromSeconds(5));

            // A long-archived fleet is hard-deleted.
            await repo.TouchActivityAsync(fresh, now - TimeSpan.FromHours(25), ct);
            var purged = await runner.SweepAsync(now, opts, ct);
            ok &= Check("long-archived fleet is hard-deleted", await repo.GetAsync(fresh, ct) is null);
            ok &= Check("sweep reports the delete", purged.Deleted >= 1);
            if (await repo.GetAsync(fresh, ct) is null)
                created.Remove(fresh);
        }
        finally
        {
            foreach (var id in created)
                await repo.DeleteAsync(id, ct);
        }

        return ok;
    }

    // Flip a fleet to Concluded directly (the cleanup-eligible phase) without the Start→Conclude command dance or its
    // roster notifications — the sweep only reads Activation, so the phase is all that matters here.
    private static async Task ConcludeAsync(IFleetRepository repo, long fleetId, CancellationToken ct)
    {
        var fleet = await repo.GetAsync(fleetId, ct);
        fleet!.Activation = FleetActivation.Concluded;
        await repo.UpdateAsync(fleet, ct);
    }

    private static bool Check(string label, bool pass)
    {
        Console.WriteLine($"  {(pass ? "✓" : "✗ FAIL")}  {label}");
        return pass;
    }

    // A connected client only needs a stream writer to register presence; this one discards everything.
    private sealed class NoopWriter : IServerStreamWriter<ServerEnvelope>
    {
        public WriteOptions? WriteOptions { get; set; }
        public Task WriteAsync(ServerEnvelope message) => Task.CompletedTask;
        public Task WriteAsync(ServerEnvelope message, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
