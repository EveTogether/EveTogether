using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Esi;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Fleet;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.Fleet;

/// <summary>
/// The one "remove this pilot" flow, shared by every screen that shows a fleet member (fleet metrics, the roster
/// tree and its member list, the fleets window). Removing means removing from the EVE Together fleet and nothing
/// else — the pilot keeps flying in the live in-game fleet, and that on its own is the whole action. Only when the
/// fleet is coupled to an in-game fleet AND the boss can actually write to it does a SECOND, separate confirmation
/// ask whether to kick them in-game too; declining it is a complete result. The server is the real authority on who
/// may remove whom (<c>RemoveFleetMemberCommandHandler</c> — owner-or-self, checked against the authenticated
/// character); this flow only asks, calls and reports.
/// </summary>
public sealed class FleetMemberRemovalService(IServiceProvider services, IDialogService dialogs) : ISingletonService
{
    /// <summary>
    /// Runs the whole removal for one member and reports what happened. <paramref name="fleets"/> is the calling
    /// screen's transport, so this serves a server-backed and a client-only fleet unchanged. The message is meant to
    /// be shown as-is: on the unhappy paths it names what did and did not happen, because "off the roster but still
    /// in the in-game fleet" is exactly the state an FC must not have to guess at.
    /// </summary>
    public async Task<(FleetMemberRemovalStatus Status, string Message)> RemoveAsync(
        IFleetClient fleets, FleetMemberRemovalRequest request, CancellationToken cancellationToken = default)
    {
        if (!await dialogs.ConfirmAsync(
                "Remove from fleet",
                $"Remove {request.MemberName} from '{request.FleetName}'? They leave the EVE Together fleet — the in-game fleet is not touched.",
                okText: "Remove"))
            return (FleetMemberRemovalStatus.Cancelled, "");

        var removed = await fleets.RemoveFleetMemberAsync(request.MemberId);
        if (!removed.Ok)
            return (FleetMemberRemovalStatus.Failed, $"Remove failed: {removed.Message}");

        return await _OfferInGameKickAsync(request, cancellationToken);
    }

    // The second step, and only ever the second step: the pilot is already off the EVE Together roster by the time
    // this asks anything, so every branch here reports a removal that has already happened.
    private async Task<(FleetMemberRemovalStatus Status, string Message)> _OfferInGameKickAsync(
        FleetMemberRemovalRequest request, CancellationToken cancellationToken)
    {
        string removedHere = $"{request.MemberName} was removed from the fleet.";

        if (request.EsiFleetId is not { } esiFleetId || request.EsiFleetBossId is not { } bossCharacterId
            || services.GetService<FleetEsiControlService>() is not { } control)
            return (FleetMemberRemovalStatus.RemovedFromFleet, removedHere);

        // A boss without write_fleet cannot kick anyone, so never ask a question whose only answer is an ESI error.
        if (services.GetService<IEsiScopeGate>() is not { } gate
            || !(await gate.EvaluateAsync(bossCharacterId, [FleetsScopeCatalog.WriteFleet], cancellationToken)).IsAllowed)
            return (FleetMemberRemovalStatus.RemovedFromFleet, removedHere);

        if (!await dialogs.ConfirmAsync(
                "Remove from the in-game fleet too?",
                $"{request.MemberName} is off the EVE Together roster. This fleet is coupled to an in-game fleet — "
                + "kick them out of that one as well?",
                okText: "Kick in-game"))
            return (FleetMemberRemovalStatus.RemovedFromFleetInGameDeclined,
                $"{removedHere} They are still in the in-game fleet.");

        var kicked = await control.KickMemberAsync(esiFleetId, bossCharacterId, request.CharacterId, cancellationToken);
        if (kicked.IsSuccess)
            return (FleetMemberRemovalStatus.RemovedFromFleetAndInGame,
                $"{removedHere} Kicked from the in-game fleet too.");

        string reason = kicked.Messages.FirstOrDefault()?.Text ?? "the in-game kick failed";
        return (FleetMemberRemovalStatus.RemovedFromFleetInGameFailed,
            $"{removedHere} The in-game kick did NOT go through ({reason}) — they are still in the in-game fleet.");
    }
}
