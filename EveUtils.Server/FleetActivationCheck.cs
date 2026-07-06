using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Commands;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Queries;
using EveUtils.Shared.Modules.Messaging.Entities;
using EveUtils.Shared.Modules.Messaging.Queries;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using FleetEntity = EveUtils.Shared.Modules.Fleet.Entities.Fleet;

namespace EveUtils.Server;

/// <summary>
/// Headless proof for the fleet-activation lifecycle, runnable via <c>--fleet-activation-test</c>.
/// Drives the real DI container + dispatcher through create (Forming) → member-join → owner-Start (Active +
/// roster notified) → reject non-creator → reject on an archived fleet → idempotent second Start (no second
/// notification). Distinct from <see cref="FleetState"/> (the soft-delete lifecycle). Exit 0 = all passed.
/// </summary>
public static class FleetActivationCheck
{
    private const int Creator = 4101;
    private const int Member = 4202;
    private const int Other = 4303;

    public static async Task<int> RunAsync(IServiceProvider services)
    {
        Console.WriteLine("== EVE-Utils fleet-activation check (Forming/Active + Start notify) ==");

        using var scope = services.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
        var ct = CancellationToken.None;
        var ok = true;

        // 1. Create a Public fleet (so a member can join directly) — defaults to Forming.
        var created = await dispatcher.Send(new CreateFleetCommand(
            "Activation Fleet", null, FleetVisibility.Public, null, null, FleetOfflineBehavior.StayOffline, Creator), ct);
        ok &= Check("create succeeds", created.IsSuccess && created.Value > 0);
        var id = created.Value;

        var fresh = await dispatcher.Query(new GetFleetQuery(id), ct);
        ok &= Check("new fleet is Forming", fresh is { Activation: FleetActivation.Forming, State: FleetState.Active });

        // 2. A member joins the fleet's roster.
        var join = await dispatcher.Send(new JoinFleetCommand(id, Member), ct);
        ok &= Check("member joins the roster", join.IsSuccess);

        // 3. Non-creator Start is rejected (PERMISSION_DENIED) and does not flip activation.
        var foreignStart = await dispatcher.Send(new StartFleetCommand(id, Other), ct);
        ok &= Check("non-creator Start rejected (PERMISSION_DENIED)",
            !foreignStart.IsSuccess && foreignStart.Messages.Any(m => m.Code == MessageCodes.PermissionDenied));
        var stillForming = await dispatcher.Query(new GetFleetQuery(id), ct);
        ok &= Check("fleet still Forming after rejected Start", stillForming is { Activation: FleetActivation.Forming });

        // 4. Owner Start → Active, and the member receives a single notification (creator is not notified).
        var start = await dispatcher.Send(new StartFleetCommand(id, Creator), ct);
        ok &= Check("creator can Start", start.IsSuccess);
        var afterStart = await dispatcher.Query(new GetFleetQuery(id), ct);
        ok &= Check("fleet is Active after Start", afterStart is { Activation: FleetActivation.Active });

        var memberMessages = await dispatcher.Query(new ListPendingMessagesQuery(Member), ct);
        var startNotices = memberMessages.Where(IsStartNotice).ToList();
        ok &= Check("member has exactly one Start notification (mail)", startNotices.Count == 1);

        var creatorMessages = await dispatcher.Query(new ListPendingMessagesQuery(Creator), ct);
        ok &= Check("creator is NOT notified of their own Start", creatorMessages.All(m => !IsStartNotice(m)));

        // 5. Second Start is idempotent: stays Active, no second notification.
        var secondStart = await dispatcher.Send(new StartFleetCommand(id, Creator), ct);
        ok &= Check("second Start is idempotent (Success)", secondStart.IsSuccess);
        var afterSecond = await dispatcher.Query(new ListPendingMessagesQuery(Member), ct);
        ok &= Check("no second Start notification enqueued",
            afterSecond.Count(IsStartNotice) == startNotices.Count);

        // 6. Start on an archived fleet is rejected (the Start guard refuses archived fleets).
        var disband = await dispatcher.Send(new DisbandFleetCommand(id, Creator), ct);
        ok &= Check("creator can disband", disband.IsSuccess);
        var archivedStart = await dispatcher.Send(new StartFleetCommand(id, Creator), ct);
        ok &= Check("Start on archived fleet rejected", !archivedStart.IsSuccess);

        await CleanupAsync(scope.ServiceProvider, id, ct);

        Console.WriteLine(ok ? "RESULT: PASS ✓" : "RESULT: FAIL ✗");
        return ok ? 0 : 1;
    }

    private static bool IsStartNotice(QueuedMessage m) =>
        m.Kind == MessageKind.FleetStarted && m.Title.StartsWith("Fleet started:", StringComparison.Ordinal);

    private static async Task CleanupAsync(IServiceProvider provider, long fleetId, CancellationToken ct)
    {
        await using var db = await provider.GetRequiredService<IDbContextFactory<ServerDbContext>>().CreateDbContextAsync(ct);
        var fleet = await db.Set<FleetEntity>().FirstOrDefaultAsync(f => f.Id == fleetId, ct);
        if (fleet is not null)
        {
            db.Remove(fleet); // cascade removes members
            await db.SaveChangesAsync(ct);
        }

        int[] recipients = [Creator, Member, Other];
        await db.Set<QueuedMessage>().Where(m => recipients.Contains(m.RecipientCharacterId)).ExecuteDeleteAsync(ct);
    }

    private static bool Check(string label, bool pass)
    {
        Console.WriteLine($"  {(pass ? "✓" : "✗ FAIL")}  {label}");
        return pass;
    }
}
