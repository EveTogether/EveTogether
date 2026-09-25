using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet;
using EveUtils.Shared.Modules.Fleet.Commands;
using EveUtils.Shared.Modules.Fleet.Composition;
using EveUtils.Shared.Modules.Fleet.Composition.Commands;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Enums;
using EveUtils.Shared.Modules.Fleet.Queries;
using EveUtils.Shared.Modules.Fleet.Repositories;
using Microsoft.Extensions.DependencyInjection;
using EveUtils.Shared.Modules.Skills;

namespace EveUtils.Client.Fleet;

/// <summary>
/// Client-only fleet flow. A client-only fleet lives purely in this desktop client's local SQLite,
/// is never published to a server, has no fleet browser and no remote invites/requests/notifications: it only
/// ever holds the owner's own local characters plus externals, and its metrics stay local.
///
/// Anti-splintering: there is no separate fleet model. This service is a thin orchestration over the SAME Shared
/// CQRS handlers (<see cref="CreateFleetCommand"/>/<see cref="CreateWingCommand"/>/<see cref="CreateSquadCommand"/>/
/// <see cref="MoveMemberCommand"/>/<see cref="AddExternalMemberCommand"/>/<see cref="AddLocalCharacterCommand"/>) —
/// dispatched through the client's local <see cref="IDispatcher"/> — over the SAME fleet repository, which the client
/// DI binds to the client DbContext. The only client-specific touches are the <see cref="Fleet.IsClientOnly"/> marker
/// on creation and adding the owner's own local characters as ordinary (non-external) members on trust (there is no
/// remote session to join from). No gRPC, no messaging; the commands' own signal is the only publish.
///
/// The dispatcher + repository are scoped, so each operation runs inside its own service scope (this service is
/// a host singleton, like the rest of the client's fleet services).
/// </summary>
public sealed class ClientFleetService(IServiceScopeFactory scopeFactory) : ISingletonService
{
    /// <summary>
    /// Creates a client-only fleet owned by <c>ownerCharacterId</c> (a local toon). Reuses the
    /// Shared <see cref="CreateFleetCommand"/> — which already seeds the default Wing 1 + Squad 1 and adds the
    /// owner as Fleet Commander — marked <see cref="Fleet.IsClientOnly"/>. Visibility is
    /// forced <see cref="FleetVisibility.InviteOnly"/> since a client-only fleet is never discoverable.
    /// </summary>
    /// <summary>Counts how many local fleets are coupled to each of the given compositions, for the
    /// library's "N fleets" pill, from the same local store the mutations write.</summary>
    public async Task<IReadOnlyDictionary<long, int>> CountFleetsByCompositionIdsAsync(
        IReadOnlyCollection<long> compositionIds, CancellationToken cancellationToken = default)
    {
        using var scope = scopeFactory.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IFleetReader>()
            .CountFleetsByCompositionIdsAsync(compositionIds, cancellationToken);
    }

    public Task<Result<long>> CreateLocalFleetAsync(
        string name, string? description, int ownerCharacterId, CancellationToken cancellationToken = default)
        => DispatchAsync(d => d.Send(new CreateFleetCommand(
            name, description, FleetVisibility.InviteOnly, FromTime: null, ToTime: null,
            FleetOfflineBehavior.StayOffline, ownerCharacterId, IsClientOnly: true), cancellationToken));

    /// <summary>Adds a wing to a client-only fleet via the Shared <see cref="CreateWingCommand"/>.</summary>
    public Task<Result<long>> AddWingAsync(long fleetId, string name, int ownerCharacterId, CancellationToken cancellationToken = default)
        => DispatchAsync(d => d.Send(new CreateWingCommand(fleetId, name, ownerCharacterId), cancellationToken));

    /// <summary>Adds a squad to a wing via the Shared <see cref="CreateSquadCommand"/>.</summary>
    public Task<Result<long>> AddSquadAsync(long wingId, string name, int ownerCharacterId, CancellationToken cancellationToken = default)
        => DispatchAsync(d => d.Send(new CreateSquadCommand(wingId, name, ownerCharacterId), cancellationToken));

    /// <summary>Adds an external EVE character (no local session) on trust, via the Shared
    /// <see cref="AddExternalMemberCommand"/>. The one remote-ish concept a client-only fleet keeps.</summary>
    public Task<Result<long>> AddExternalAsync(
        long fleetId, int characterId, int ownerCharacterId, CancellationToken cancellationToken = default)
        => DispatchAsync(d => d.Send(new AddExternalMemberCommand(fleetId, characterId, ownerCharacterId), cancellationToken));

    /// <summary>Moves a roster member to a wing/squad with a role via the Shared <see cref="MoveMemberCommand"/>.</summary>
    public Task<Result> MoveMemberAsync(
        long memberId, FleetRole role, long wingId, long squadId, int ownerCharacterId, CancellationToken cancellationToken = default)
        => DispatchAsync(d => d.Send(new MoveMemberCommand(memberId, role, wingId, squadId, ownerCharacterId), cancellationToken));

    /// <summary>Swaps two members' roster positions via the Shared <see cref="SwapMembersCommand"/> (stream G drag-and-drop
    /// onto an occupied commander slot).</summary>
    public Task<Result> SwapMembersAsync(
        long firstMemberId, long secondMemberId, int ownerCharacterId, CancellationToken cancellationToken = default)
        => DispatchAsync(d => d.Send(new SwapMembersCommand(firstMemberId, secondMemberId, ownerCharacterId), cancellationToken));

    /// <summary>Assigns/clears a member's fit via the Shared <see cref="AssignMemberFitCommand"/>.</summary>
    public Task<Result> AssignMemberFitAsync(
        long memberId, FitReference? fit, long? compositionEntryId, int ownerCharacterId, CancellationToken cancellationToken = default)
        => DispatchAsync(d => d.Send(new AssignMemberFitCommand(memberId, fit, compositionEntryId, ownerCharacterId), cancellationToken));

    /// <summary>Stores the pilot's can-fly verdict for their assigned fit via the Shared
    /// <see cref="ReportMemberFitVerdictCommand"/>.</summary>
    public Task<Result<bool>> ReportMemberFitVerdictAsync(
        long memberId, FitSkillVerdict verdict, int actingCharacterId, CancellationToken cancellationToken = default)
        => DispatchAsync(d => d.Send(new ReportMemberFitVerdictCommand(memberId, verdict, actingCharacterId), cancellationToken));

    /// <summary>Records the pilot's own in-game fleet presence via the Shared
    /// <see cref="ReportMemberInGameFleetCommand"/>.</summary>
    public Task<Result<bool>> ReportMemberInGameFleetAsync(
        long memberId, bool inFleet, int actingCharacterId, CancellationToken cancellationToken = default)
        => DispatchAsync(d => d.Send(new ReportMemberInGameFleetCommand(memberId, inFleet, actingCharacterId), cancellationToken));

    /// <summary>Signs the acting pilot off this fleet's next start, or reverses it, via the Shared
    /// <see cref="SetFleetMemberAvailabilityCommand"/>. Self-only, strictly — <paramref name="actingCharacterId"/>
    /// must be the member's own character even for a client-only fleet's owner.</summary>
    public Task<Result> SetFleetMemberAvailabilityAsync(
        long memberId, FleetMemberAvailability availability, string? note, int actingCharacterId, CancellationToken cancellationToken = default)
        => DispatchAsync(d => d.Send(new SetFleetMemberAvailabilityCommand(memberId, availability, note, actingCharacterId), cancellationToken));

    /// <summary>Renames a wing via the Shared <see cref="RenameWingCommand"/> (client-only roster management).</summary>
    public Task<Result> RenameWingAsync(long wingId, string name, int ownerCharacterId, CancellationToken cancellationToken = default)
        => DispatchAsync(d => d.Send(new RenameWingCommand(wingId, name, ownerCharacterId), cancellationToken));

    /// <summary>Renames a squad via the Shared <see cref="RenameSquadCommand"/>.</summary>
    public Task<Result> RenameSquadAsync(long squadId, string name, int ownerCharacterId, CancellationToken cancellationToken = default)
        => DispatchAsync(d => d.Send(new RenameSquadCommand(squadId, name, ownerCharacterId), cancellationToken));

    /// <summary>Deletes a wing via the Shared <see cref="DeleteWingCommand"/> (client-only roster management).</summary>
    public Task<Result> DeleteWingAsync(long wingId, int ownerCharacterId, CancellationToken cancellationToken = default)
        => DispatchAsync(d => d.Send(new DeleteWingCommand(wingId, ownerCharacterId), cancellationToken));

    /// <summary>Deletes a squad via the Shared <see cref="DeleteSquadCommand"/>.</summary>
    public Task<Result> DeleteSquadAsync(long squadId, int ownerCharacterId, CancellationToken cancellationToken = default)
        => DispatchAsync(d => d.Send(new DeleteSquadCommand(squadId, ownerCharacterId), cancellationToken));

    /// <summary>Removes a member entirely via the Shared <see cref="RemoveFleetMemberCommand"/> (kick).</summary>
    public Task<Result> RemoveMemberAsync(long memberId, int ownerCharacterId, CancellationToken cancellationToken = default)
        => DispatchAsync(d => d.Send(new RemoveFleetMemberCommand(memberId, ownerCharacterId), cancellationToken));

    /// <summary>Transfers ownership to another local member via the Shared <see cref="TransferFleetOwnershipCommand"/>.</summary>
    public Task<Result> TransferOwnershipAsync(long fleetId, int newOwnerCharacterId, int ownerCharacterId, CancellationToken cancellationToken = default)
        => DispatchAsync(d => d.Send(new TransferFleetOwnershipCommand(fleetId, newOwnerCharacterId, ownerCharacterId), cancellationToken));

    /// <summary>Starts the fleet (Forming → Active) via the Shared <see cref="StartFleetCommand"/>.</summary>
    /// <summary>Which of a local fleet's members already count for another <i>local</i> started fleet (ET-168).
    /// Local fleets are the owner's own pilots, so this is the collision between two of your own ops; a collision
    /// with a server fleet is not visible from here and is worked out by the overview, which sees both.</summary>
    public Task<IReadOnlyList<FleetMemberElsewhereInfo>> ListMembersActiveElsewhereAsync(long fleetId, CancellationToken cancellationToken = default)
        => DispatchAsync(d => d.Query(new ListMembersActiveElsewhereQuery(fleetId), cancellationToken));

    public Task<Result> StartFleetAsync(long fleetId, int ownerCharacterId, CancellationToken cancellationToken = default)
        => DispatchAsync(d => d.Send(new StartFleetCommand(fleetId, ownerCharacterId), cancellationToken));

    /// <summary>Stops the fleet (Active → Forming, roster kept) via the Shared <see cref="StopFleetCommand"/>.
    /// <paramref name="trigger"/> is <see cref="FleetStopTrigger.Manual"/> for a pressed button and carries the
    /// reason when <see cref="LocalFleetAutoStopService"/> stands a client-only fleet down by itself (ET-167).</summary>
    public Task<Result> StopFleetAsync(
        long fleetId,
        int ownerCharacterId,
        FleetStopTrigger trigger = FleetStopTrigger.Manual,
        CancellationToken cancellationToken = default)
        => DispatchAsync(d => d.Send(new StopFleetCommand(fleetId, ownerCharacterId, trigger), cancellationToken));

    /// <summary>Concludes the fleet (→ Concluded, kept for history) via the Shared <see cref="ConcludeFleetCommand"/>.</summary>
    public Task<Result> ConcludeFleetAsync(long fleetId, int ownerCharacterId, CancellationToken cancellationToken = default)
        => DispatchAsync(d => d.Send(new ConcludeFleetCommand(fleetId, ownerCharacterId), cancellationToken));

    /// <summary>Disbands a client-only fleet (soft-delete → Archived) via the Shared <see cref="DisbandFleetCommand"/>.</summary>
    public Task<Result> DisbandFleetAsync(long fleetId, int ownerCharacterId, CancellationToken cancellationToken = default)
        => DispatchAsync(d => d.Send(new DisbandFleetCommand(fleetId, ownerCharacterId), cancellationToken));

    /// <summary>Couples/unlinks a composition on a fleet via the Shared <see cref="SetFleetCompositionCommand"/>.</summary>
    public Task<Result> SetFleetCompositionAsync(long fleetId, long? compositionId, int ownerCharacterId, CancellationToken cancellationToken = default)
        => DispatchAsync(d => d.Send(new SetFleetCompositionCommand(fleetId, compositionId, ownerCharacterId), cancellationToken));

    public Task<Result> CoupleFleetToEsiAsync(long fleetId, long esiFleetId, int esiFleetBossId, int ownerCharacterId, CancellationToken cancellationToken = default)
        => DispatchAsync(d => d.Send(new CoupleFleetToEsiCommand(fleetId, esiFleetId, esiFleetBossId, ownerCharacterId), cancellationToken));

    /// <summary>Clears a client-only fleet's stored in-game link via the Shared <see cref="UncoupleFleetFromEsiCommand"/>.</summary>
    public Task<Result> UncoupleFleetFromEsiAsync(long fleetId, int ownerCharacterId, CancellationToken cancellationToken = default)
        => DispatchAsync(d => d.Send(new UncoupleFleetFromEsiCommand(fleetId, ownerCharacterId), cancellationToken));

    /// <summary>Persists a fleet's Auto Apply / Auto Invite toggles via the Shared <see cref="SetFleetEsiAutomationCommand"/>.</summary>
    public Task<Result> SetFleetEsiAutomationAsync(
        long fleetId, bool autoApplyStructure, bool autoInviteMembers, int ownerCharacterId, CancellationToken cancellationToken = default)
        => DispatchAsync(d => d.Send(
            new SetFleetEsiAutomationCommand(fleetId, ownerCharacterId, autoApplyStructure, autoInviteMembers), cancellationToken));

    /// <summary>Adds one of the owner's own local characters to a client-only fleet via the Shared
    /// <see cref="AddLocalCharacterCommand"/>.</summary>
    public Task<Result<long>> AddLocalCharacterAsync(
        long fleetId, int characterId, int ownerCharacterId, CancellationToken cancellationToken = default)
        => DispatchAsync(d => d.Send(new AddLocalCharacterCommand(fleetId, characterId, ownerCharacterId), cancellationToken));

    // --- Fleet Compositions. Client-only library: thin orchestration over the SAME Shared CQRS
    // composition handlers via the local dispatcher. Reads are served straight from the repository by the facade. ---

    public Task<Result<long>> CreateCompositionAsync(string name, string? description, bool isClientOnly, int ownerCharacterId, CancellationToken cancellationToken = default)
        => DispatchAsync(d => d.Send(new CreateFleetCompositionCommand(name, description, isClientOnly, ownerCharacterId), cancellationToken));

    public Task<Result> EditCompositionAsync(long compositionId, string name, string? description, int actingCharacterId, CancellationToken cancellationToken = default)
        => DispatchAsync(d => d.Send(new EditFleetCompositionCommand(compositionId, name, description, actingCharacterId), cancellationToken));

    public Task<Result> DeleteCompositionAsync(long compositionId, int actingCharacterId, CancellationToken cancellationToken = default)
        => DispatchAsync(d => d.Send(new DeleteFleetCompositionCommand(compositionId, actingCharacterId), cancellationToken));

    public Task<Result<long>> AddCompositionRoleAsync(long compositionId, string roleName, int? groupMinCount, int actingCharacterId, CancellationToken cancellationToken = default)
        => DispatchAsync(d => d.Send(new AddFleetCompositionRoleCommand(compositionId, roleName, groupMinCount, actingCharacterId), cancellationToken));

    public Task<Result> EditCompositionRoleAsync(long roleId, string roleName, int? groupMinCount, int actingCharacterId, CancellationToken cancellationToken = default)
        => DispatchAsync(d => d.Send(new EditFleetCompositionRoleCommand(roleId, roleName, groupMinCount, actingCharacterId), cancellationToken));

    public Task<Result> RemoveCompositionRoleAsync(long roleId, int actingCharacterId, CancellationToken cancellationToken = default)
        => DispatchAsync(d => d.Send(new RemoveFleetCompositionRoleCommand(roleId, actingCharacterId), cancellationToken));

    public Task<Result> ReorderCompositionRolesAsync(long compositionId, IReadOnlyList<long> orderedRoleIds, int actingCharacterId, CancellationToken cancellationToken = default)
        => DispatchAsync(d => d.Send(new ReorderFleetCompositionRolesCommand(compositionId, orderedRoleIds, actingCharacterId), cancellationToken));

    public Task<Result<long>> AddCompositionEntryAsync(long roleId, FitReference fit, int? entryMinCount, IReadOnlyList<SkillMinimum> skillMinimums,
        int actingCharacterId, CancellationToken cancellationToken = default)
        => DispatchAsync(d => d.Send(new AddFleetCompositionEntryCommand(roleId, fit, entryMinCount, actingCharacterId, skillMinimums), cancellationToken));

    public Task<Result> EditCompositionEntryAsync(long entryId, int? entryMinCount, IReadOnlyList<SkillMinimum>? skillMinimums,
        int actingCharacterId, CancellationToken cancellationToken = default)
        => DispatchAsync(d => d.Send(new EditFleetCompositionEntryCommand(entryId, entryMinCount, actingCharacterId, skillMinimums), cancellationToken));

    public Task<Result> RemoveCompositionEntryAsync(long entryId, int actingCharacterId, CancellationToken cancellationToken = default)
        => DispatchAsync(d => d.Send(new RemoveFleetCompositionEntryCommand(entryId, actingCharacterId), cancellationToken));

    public Task<Result> ReorderCompositionEntriesAsync(long roleId, IReadOnlyList<long> orderedEntryIds, int actingCharacterId, CancellationToken cancellationToken = default)
        => DispatchAsync(d => d.Send(new ReorderFleetCompositionEntriesCommand(roleId, orderedEntryIds, actingCharacterId), cancellationToken));

    private async Task<T> DispatchAsync<T>(Func<IDispatcher, Task<T>> operation)
    {
        using var scope = scopeFactory.CreateScope();
        return await operation(scope.ServiceProvider.GetRequiredService<IDispatcher>());
    }
}
