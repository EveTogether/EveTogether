using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Repositories;
using FleetEntity = EveUtils.Shared.Modules.Fleet.Entities.Fleet;

namespace EveUtils.Shared.Modules.Fleet.Commands;

/// <summary>
/// Shared creator-authorization for fleet management handlers (structure and invites). Resolves the owning
/// fleet and verifies it exists, is still active and is owned by the acting character. Role-based
/// authorization (FC/WC/SC) is a reserved seam that activates once the roster exists; for now the
/// creator is the sole authority.
/// </summary>
internal static class FleetStructureGuard
{
    public static async Task<Result<FleetEntity>> ResolveOwnedActiveFleetAsync(
        IFleetRepository repository, long fleetId, int actingCharacterId, CancellationToken cancellationToken)
    {
        var fleet = await repository.GetAsync(fleetId, cancellationToken);
        if (fleet is null)
            return Result<FleetEntity>.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.NotFound, "Fleet not found.", "Fleet"));

        if (fleet.State == FleetState.Archived)
            return Result<FleetEntity>.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.ValidationFailed, "Cannot modify an archived fleet.", "Fleet"));

        if (fleet.CreatorCharacterId != actingCharacterId)
            return Result<FleetEntity>.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.PermissionDenied, "Only the fleet's creator can manage it.", "Fleet"));

        return Result<FleetEntity>.Success(fleet);
    }

    /// <summary>
    /// Authorization for setting the fit a member flies: the fleet creator may assign it
    /// top-down, OR the member may set their OWN fit (no manage right needed) — every pilot picks their own ship.
    /// Resolves the owning fleet and verifies it exists and is not archived. Distinct from
    /// <see cref="ResolveOwnedActiveFleetAsync"/>, which is creator-only.
    /// </summary>
    public static async Task<Result<FleetEntity>> ResolveFleetForMemberFitAsync(
        IFleetRepository repository, long fleetId, int actingCharacterId, int memberCharacterId, CancellationToken cancellationToken)
    {
        var fleet = await repository.GetAsync(fleetId, cancellationToken);
        if (fleet is null)
            return Result<FleetEntity>.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.NotFound, "Fleet not found.", "Fleet"));

        if (fleet.State == FleetState.Archived)
            return Result<FleetEntity>.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.ValidationFailed, "Cannot modify an archived fleet.", "Fleet"));

        if (fleet.CreatorCharacterId != actingCharacterId && memberCharacterId != actingCharacterId)
            return Result<FleetEntity>.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.PermissionDenied,
                "Only the fleet's creator or the member themselves can set this member's fit.", "Fleet"));

        return Result<FleetEntity>.Success(fleet);
    }
}
